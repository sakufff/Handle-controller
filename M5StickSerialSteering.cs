using UnityEngine;
using System;
using System.IO.Ports;
using System.Threading;
using System.Globalization;
using UnityEngine.Events;
using System.Linq;
using System.Text;

public class M5StickSerialSteering : MonoBehaviour
{
    [Header("Serial")]
    [Tooltip("空欄なら自動スキャンします")]
    public string portName = "";                 // 例: "COM15" 固定するなら指定、空で自動
    public int baudRate = 115200;
    [Tooltip("優先して試す候補（空欄時）。空でもOK、GetPortNames()を全部試します")]
    public string[] candidatePorts = new[] { "COM15", "COM16", "COM11", "COM10", "COM5", "COM4" };
    public bool autoReconnect = true;
    public int reconnectDelayMs = 1500;
    [Tooltip("ポート選定時、受信を待つ最大ms")]
    public int probeWaitMs = 2000;

    [Header("Steer Mapping")]
    public float inputMinDeg = -30f;
    public float inputMaxDeg = 30f;
    public float deadZoneDeg = 1.0f;
    [Range(0, 1)] public float smoothing = 0.15f;
    public float sensitivity = 1.0f;
    public bool invertSteer = false;

    [Header("Outputs (read only)")]
    public float rollDegRaw;
    public float rollDegFiltered;
    public float steerNormalized;   // -1..1
    public bool buttonPressed;      // BTN
    public bool auxButtonPressed;   // BTN2 (G0)
    public float throttleNormalized;// 0..1 (THR)

    [Header("Events (optional)")]
    public UnityEvent<float> OnSteerChanged;
    public UnityEvent<bool> OnButtonChanged;      // BTN
    public UnityEvent<bool> OnAuxButtonChanged;   // BTN2
    public UnityEvent<float> OnThrottleChanged;   // THR

    [Header("Debug")]
    public bool debugDownlinkLog = false;   // Unity→M5
    public bool debugUplinkLog = false;     // M5→Unity
    public bool debugProbeLog = true;       // ポート探索ログ

    SerialPort _sp;
    Thread _thread;
    volatile bool _running;

    // thread buffers
    volatile float _rollThreadRaw;
    volatile int _btnThread = 0;     // BTN:0/1
    volatile int _btn2Thread = 0;    // BTN2:0/1
    volatile float _thrThread = 0f;  // THR:0..1

    float _steerSmoothed;

    readonly object _writeLock = new object();
    readonly object _openCloseLock = new object();

    public bool IsPortOpen => _sp != null && _sp.IsOpen;
    public string CurrentPort => _sp != null ? _sp.PortName : "";

    // ====== 速度送信用（SPD:<0..999>） ======
    public bool SetSpeedKmh(int kmh)
    {
        kmh = Mathf.Clamp(kmh, 0, 999);
        return SendLine($"SPD:{kmh}");
    }

    // ====== M5へ1行送信 ======
    public bool SendLine(string line)
    {
        try
        {
            var sp = _sp;
            if (sp == null || !sp.IsOpen) return false;
            lock (_writeLock)
            {
                sp.WriteLine(line);   // NewLine = "\n"
            }
            if (debugDownlinkLog) Debug.Log($"[M5<-Unity {sp.PortName}] {line}");
            return true;
        }
        catch (Exception e)
        {
            if (debugDownlinkLog) Debug.LogWarning("[M5 send fail] " + e.Message);
            return false;
        }
    }

    void Start() => TryOpenSerial();

    void OnDestroy()
    {
        _running = false;
        try { _thread?.Join(300); } catch { }
        SafeClose();
    }

    // ====== ポートを開く（固定名→候補→OS列挙の順） ======
    void TryOpenSerial()
    {
        lock (_openCloseLock)
        {
            SafeClose();

            string[] baseList;
            if (!string.IsNullOrEmpty(portName))
                baseList = new[] { portName };
            else
            {
                // 候補 + OS列挙（重複除去、数字降順＝新しい順）
                var osPorts = SerialPort.GetPortNames()
                                        .Where(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                                        .OrderByDescending(p => ParseComNumber(p))
                                        .ToArray();
                baseList = (candidatePorts ?? Array.Empty<string>())
                           .Concat(osPorts)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToArray();
            }

            if (debugProbeLog)
                Debug.Log("[M5] Probe list: " + string.Join(", ", baseList));

            foreach (var pn in baseList)
            {
                if (string.IsNullOrEmpty(pn)) continue;

                // SPP相性対策：DTR/RTS を OFF → ON の2パターンで試す
                var dtrRtsCombos = new (bool dtr, bool rts)[] { (false, false), (true, true) };

                foreach (var combo in dtrRtsCombos)
                {
                    if (debugProbeLog) Debug.Log($"[M5] Try {pn} (DTR={combo.dtr}, RTS={combo.rts})");

                    if (OpenAndProbe(pn, combo.dtr, combo.rts))
                    {
                        if (debugProbeLog) Debug.Log($"[M5] Connected: {pn}");
                        portName = pn;        // 成功したポート名を保持
                        return;
                    }
                }
            }

            Debug.LogError("[M5] No working COM port found. (Close Arduino Serial Monitor / pick correct COM)");
            if (autoReconnect) Invoke(nameof(DeferredReconnect), reconnectDelayMs / 1000f);
        }
    }

    int ParseComNumber(string s)
    {
        // "COM15" → 15
        for (int i = 3; i < s.Length; i++)
            if (char.IsDigit(s[i])) return int.Parse(s.Substring(i));
        return -1;
    }

    void DeferredReconnect()
    {
        if (!_running && !IsPortOpen) TryOpenSerial();
    }

    bool OpenAndProbe(string pn, bool dtr, bool rts)
    {
        try
        {
            var sp = new SerialPort(pn, baudRate)
            {
                NewLine = "\n",
                ReadTimeout = 100,
                WriteTimeout = 1000,
                Handshake = Handshake.None,
                DtrEnable = dtr,
                RtsEnable = rts,
                Parity = Parity.None,
                StopBits = StopBits.One,
                DataBits = 8,
                Encoding = Encoding.ASCII
            };

            sp.Open();

            // ここで短時間だけ直接 ReadLine して「本当にM5から何か来るか」確認
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < probeWaitMs)
            {
                try
                {
                    string line = sp.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(line))
                    {
                        if (debugProbeLog) Debug.Log($"[M5 Probe {pn}] '{line}'");
                        if (IsM5Line(line))
                        {
                            // 受信OK → 本接続に昇格
                            _sp = sp;
                            _running = true;
                            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "M5SerialRead" };
                            _thread.Start();
                            return true;
                        }
                    }
                }
                catch (TimeoutException) { /* 待ち続ける */ }
                catch (Exception e)
                {
                    if (debugProbeLog) Debug.LogWarning($"[M5 Probe {pn}] fail: {e.Message}");
                    break;
                }
            }

            // 受信が確認できず → クローズして失敗扱い
            try { sp.Close(); } catch { }
            return false;
        }
        catch (Exception e)
        {
            if (debugProbeLog) Debug.LogWarning($"[M5 Open {pn}] {e.Message}");
            return false;
        }
    }

    bool IsM5Line(string line)
    {
        if (line.StartsWith("BTN:", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("BTN2:", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("THR:", StringComparison.OrdinalIgnoreCase)) return true;
        // roll は数値のみ
        var ci = CultureInfo.InvariantCulture;
        return float.TryParse(line, System.Globalization.NumberStyles.Float, ci, out _);
    }

    void SafeClose()
    {
        try { if (_sp != null && _sp.IsOpen) _sp.Close(); } catch { }
        _sp = null;
        _running = false;
    }

    // ====== 受信スレッド ======
    void ReadLoop()
    {
        var ci = CultureInfo.InvariantCulture;
        try
        {
            while (_running && _sp != null && _sp.IsOpen)
            {
                try
                {
                    string line = _sp.ReadLine();
                    if (line == null) continue;
                    line = line.Trim();
                    if (debugUplinkLog && line.Length > 0)
                        Debug.Log($"[M5->Unity {_sp.PortName}] {line}");

                    // BTN:0/1
                    if (line.StartsWith("BTN:", StringComparison.OrdinalIgnoreCase))
                    {
                        _btnThread = (line.EndsWith("1")) ? 1 : 0;
                        continue;
                    }
                    // BTN2:0/1
                    if (line.StartsWith("BTN2:", StringComparison.OrdinalIgnoreCase))
                    {
                        _btn2Thread = (line.EndsWith("1")) ? 1 : 0;
                        continue;
                    }
                    // THR:0.000
                    if (line.StartsWith("THR:", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(4);
                        if (float.TryParse(v, NumberStyles.Float, ci, out float thr))
                            _thrThread = Mathf.Clamp01(thr);
                        continue;
                    }
                    // ROLL（数値のみの行）
                    if (float.TryParse(line, NumberStyles.Float, ci, out float deg))
                    {
                        _rollThreadRaw = deg;
                        continue;
                    }
                }
                catch (TimeoutException)
                {
                    // 読み取りなし → 継続
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[M5] Read error: " + e.Message);
                    break; // → 再接続へ
                }
            }
        }
        finally
        {
            try { if (_sp != null && _sp.IsOpen) _sp.Close(); } catch { }
            _sp = null;
            _running = false;

            if (autoReconnect)
            {
                Debug.Log("[M5] Reconnecting...");
                Thread.Sleep(reconnectDelayMs);
                UnityMainThreadPost(TryOpenSerial);
            }
        }
    }

    // メインスレッドに処理を戻す簡易ポスト
    void UnityMainThreadPost(Action act)
    {
        lock (_postLock) { _postAction = act; _hasPost = true; }
    }
    Action _postAction; bool _hasPost; readonly object _postLock = new object();

    void Update()
    {
        // メインスレッドポスト実行
        if (_hasPost)
        {
            Action a = null;
            lock (_postLock) { a = _postAction; _postAction = null; _hasPost = false; }
            a?.Invoke();
        }

        // --- Roll / Steer ---
        rollDegRaw = _rollThreadRaw;
        rollDegFiltered = Mathf.Lerp(rollDegFiltered, rollDegRaw, 1f - smoothing);

        float deg = (Mathf.Abs(rollDegFiltered) < deadZoneDeg) ? 0f : rollDegFiltered;
        float t = Mathf.InverseLerp(inputMinDeg, inputMaxDeg, deg); // 0..1
        float steer = Mathf.Lerp(-1f, 1f, t) * sensitivity;         // -1..1
        if (invertSteer) steer = -steer;
        steer = Mathf.Clamp(steer, -1f, 1f);

        _steerSmoothed = Mathf.Lerp(_steerSmoothed, steer, 1f - smoothing);
        if (!Mathf.Approximately(_steerSmoothed, steerNormalized))
        {
            steerNormalized = _steerSmoothed;
            OnSteerChanged?.Invoke(steerNormalized);
        }

        // --- Buttons ---
        bool newPressed = (_btnThread != 0);
        bool auxNow = (_btn2Thread != 0);

        if (newPressed != buttonPressed)
        {
            buttonPressed = newPressed;
            OnButtonChanged?.Invoke(buttonPressed);
        }
        if (auxNow != auxButtonPressed)
        {
            auxButtonPressed = auxNow;
            OnAuxButtonChanged?.Invoke(auxButtonPressed);
        }

        // --- Throttle (0..1) ---
        float thr = _thrThread;
        if (!Mathf.Approximately(thr, throttleNormalized))
        {
            throttleNormalized = thr;
            OnThrottleChanged?.Invoke(throttleNormalized);
        }
    }
}
