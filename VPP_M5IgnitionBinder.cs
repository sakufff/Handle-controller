using UnityEngine;
using System;
using System.Reflection;

[DisallowMultipleComponent]
[AddComponentMenu("Vehicle Physics/Utility/VPP M5 Ignition Binder (Simple)")]
public class VPP_M5IgnitionBinder_Simple : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("車両の VPStandardInput コンポーネントをドラッグ")]
    public Component standardInput;
    [Tooltip("M5StickSerialSteering をドラッグ（G0=auxButtonPressed を使う）")]
    public M5StickSerialSteering source;

    [Header("Behavior")]
    public IgnLevel pressLevel = IgnLevel.Start;   // 押した瞬間に送るレベル
    public float startHoldSeconds = 0.25f;       // Start を保持する時間
    public IgnLevel afterStartLevel = IgnLevel.On; // Start パルス後に戻すレベル

    [Header("Safety / Debounce")]
    [Tooltip("起動直後、ボタンが一定時間『離されている(false)』の状態を確認してから有効化")]
    public float armWhenReleasedSeconds = 0.3f;
    [Tooltip("起動時に externalIgnition を強制的に Off にします")]
    public bool forceOffOnAwake = true;

    public enum IgnLevel { Off = 0, Acc = 1, On = 2, Start = 3 }

    // reflection 書き込み
    Action<object> _setIgn;
    Func<object> _getIgn;
    Type _ignType;
    bool _isBool;

    // 状態
    bool _prevBtn = false;
    float _pulseTimer = 0f;
    bool _armed = false;
    float _releasedTimer = 0f;

    void Reset()
    {
#if UNITY_2022_2_OR_NEWER
        if (standardInput == null) standardInput = FindAnyObjectByType<Component>(FindObjectsInactive.Include);
        if (source == null) source = FindAnyObjectByType<M5StickSerialSteering>(FindObjectsInactive.Include);
#else
        if (standardInput == null) standardInput = FindObjectOfType<Component>();
        if (source == null)        source        = FindObjectOfType<M5StickSerialSteering>();
#endif
    }

    void Awake()
    {
        if (standardInput == null || source == null)
        {
            Debug.LogWarning("[IgnBinder] refs not set. Drag VPStandardInput and M5StickSerialSteering.", this);
            enabled = false;
            return;
        }

        // externalIgnition / ignition / （大文字小文字無視）を探す
        var t = standardInput.GetType();
        string[] names = { "externalIgnition", "ignition" };

        MemberInfo member = null;
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (p != null) { member = p; break; }
        }
        if (member == null)
        {
            foreach (var n in names)
            {
                var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (f != null) { member = f; break; }
            }
        }
        if (member == null)
        {
            Debug.LogWarning($"[IgnBinder] externalIgnition not found on {t.Name}.", this);
            enabled = false;
            return;
        }

        if (member is PropertyInfo pi)
        {
            _ignType = pi.PropertyType;
            _setIgn = v => pi.SetValue(standardInput, v, null);
            _getIgn = () => pi.GetValue(standardInput, null);
        }
        else
        {
            var fi = (FieldInfo)member;
            _ignType = fi.FieldType;
            _setIgn = v => fi.SetValue(standardInput, v);
            _getIgn = () => fi.GetValue(standardInput);
        }

        _isBool = (_ignType == typeof(bool));

        // ★起動時は必ず Off に固定（勝手始動防止）
        if (forceOffOnAwake)
        {
            if (_isBool) _setIgn(false);
            else SetIgnEnumLike(IgnLevel.Off);
        }
    }

    void Update()
    {
        if (source == null) return;

        bool btn = source.auxButtonPressed; // G0

        // --- アーム（離し状態が一定時間続くまで待つ）
        if (!_armed)
        {
            if (!btn)
            {
                _releasedTimer += Time.deltaTime;
                if (_releasedTimer >= armWhenReleasedSeconds)
                    _armed = true;
            }
            else
            {
                _releasedTimer = 0f;
            }
        }

        // --- 立ち上がり検出（アーム後のみ）
        if (_armed && btn && !_prevBtn)
        {
            if (_isBool)
            {
                // bool は 1フレームだけ true
                _setIgn(true);
                _pulseTimer = 0.02f;
            }
            else
            {
                // enum/数値は Start → afterStartLevel に戻す
                SetIgnEnumLike(pressLevel);      // Start
                _pulseTimer = Mathf.Max(0f, startHoldSeconds);
            }
        }

        // --- パルス解除
        if (_pulseTimer > 0f)
        {
            _pulseTimer -= Time.deltaTime;
            if (_pulseTimer <= 0f)
            {
                if (_isBool) _setIgn(false);
                else SetIgnEnumLike(afterStartLevel); // 通常は On
            }
        }

        // ボタンを離した後の安定値（bool は false、enum は afterStartLevel）
        if (!btn && _prevBtn && _pulseTimer <= 0f)
        {
            if (_isBool) _setIgn(false);
            else SetIgnEnumLike(afterStartLevel);
        }

        _prevBtn = btn;
    }

    // enum/数値型 externalIgnition への安全な書き込み
    void SetIgnEnumLike(IgnLevel level)
    {
        object val;
        if (_ignType.IsEnum)
        {
            try { val = Enum.Parse(_ignType, level.ToString(), true); }
            catch { val = Enum.ToObject(_ignType, (int)level); }
        }
        else if (_ignType == typeof(int) || _ignType == typeof(short) ||
                 _ignType == typeof(byte) || _ignType == typeof(long))
        {
            val = Convert.ChangeType((int)level, _ignType);
        }
        else if (_ignType == typeof(float) || _ignType == typeof(double))
        {
            val = Convert.ChangeType((int)level, _ignType);
        }
        else
        {
            val = (int)level;
        }
        _setIgn(val);
    }
}
