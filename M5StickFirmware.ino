#include <M5Unified.h>
#include <driver/adc.h>
#include <math.h>
#include <string.h>
#include <BluetoothSerial.h>   // ★ 追加：Bluetooth SPP

/* ================= Colors ================= */
uint32_t COLOR_LIME;    // #32CD32 (speed)
uint32_t COLOR_ORANGE;  // #FFA500 (fuel normal)
uint32_t COLOR_RED;     // #FF3B30 (fuel last block)
uint32_t COLOR_WHITE;   // #FFFFFF
uint32_t COLOR_BLACK;   // #000000
uint32_t COLOR_OFF;     // 7seg off (dim gray)

/* ================= Fuel gauge ================= */
static const int FUEL_BLOCKS = 5;
int fuelBlocks = FUEL_BLOCKS;           // 5 → 0
uint32_t lastFuelTick = 0;
const uint32_t FUEL_INTERVAL_MS = 60UL * 1000UL; // 1分

/* ================= 7seg tuning ================= */
const float SEG_SIZE    = 10.0f;  // 文字サイズ
const float SEG_SPACING = 45.0f;  // 桁間
const float SEG_THICK   = 6.0f;   // 太さ(px)

/* ================= Layout (fuel) ================= */
const int baseX = 10;
const int baseY = 30;
const int blockW = 12;
const int blockH = 15;
const int gap    = 3;

/* ================= Speed draw area ================= */
const float SPEED_CY  = 72;
const float SPEED_CX0 = 60;

/* ================= Y方向オフセット（上へ移動：負の値） ================= */
const float TEXT_Y_OFF   = -4.0f;  // F/E, km/h
const float SPEED_Y_OFF  = -4.0f;  // 7seg数字
const float GAUGE_Y_OFF  = -4.0f;  // 燃料ゲージ

/* ================= ACC表示位置 ================= */
const int ACC_X = 200;
const int ACC_Y = 120;

/* ================== Unity I/O / センサ統合 ================== */
// ピン
static const int BTN_PIN  = 26;  // 外付けタクト①（G26-GND）→ "BTN:"
static const int BTN2_PIN = 0;   // 外付けタクト②（G0 - GND）→ "BTN2:" ※起動中は押さない
// デボウンス
static const uint32_t DEBOUNCE_MS = 20;
// アクセルADCマップ（1500〜4095 → 0..1）
static const int RAW_MIN = 1500;
static const int RAW_MAX = 4095;
// アクセル向き反転
const bool INVERT_THROTTLE = true;
// ボタン状態
bool swLastStable  = HIGH, swLastRead  = HIGH;
bool sw2LastStable = HIGH, sw2LastRead = HIGH;
uint32_t swLastChg = 0,    sw2LastChg  = 0;
// ロール角（相補フィルタ）
float roll_deg = 0.0f;     // 表示用ロール角[deg]
float zero_offset = 0.0f;  // ゼロ点オフセット
uint32_t last_ms = 0;
// Unity側取り決め：右がマイナス/左がプラス
const float roll_sign = -1.0f;

// Unity から受信した速度（km/h）
int g_speed = 0;

/* ======== Bluetooth SPP ======== */
BluetoothSerial SerialBT;  // ★ 追加：BTシリアルインスタンス

// 送信用ヘルパ（BTへ送る。デバッグでUSBにも出したいならSerial行のコメント解除）
inline void sendUnityLine(const String& s) {
  SerialBT.println(s);
  // Serial.println(s);    // ←デバッグ用にUSBへも出すなら有効化
}

/* ================= 7セグ描画 ================= */
void draw7Seg(float x0, float y0, float s, int digit, uint32_t col) {
  const float l = 3.0f * s;     // 横幅
  const float h = 5.0f * s;     // 縦幅
  const float t = SEG_THICK;    // 太さ
  const float x_left   = x0 - l * 0.5f;   // 左端
  const float y_center = y0;              // Gの中心Y座標

  struct Seg { float x1, y1, x2, y2; };
  Seg segs[7] = {
    {x_left,           y_center - h * 0.5f, x_left + l, y_center - h * 0.5f},      // A (上)
    {x_left + l,       y_center,            x_left + l, y_center - h * 0.5f},      // B (右上)
    {x_left + l,       y_center + h * 0.5f, x_left + l, y_center},                 // C (右下)
    {x_left,           y_center + h * 0.5f, x_left + l, y_center + h * 0.5f},      // D (下)
    {x_left,           y_center + h * 0.5f, x_left,     y_center},                 // E (左上)
    {x_left,           y_center,            x_left,     y_center - h * 0.5f},      // F (左下)
    {x_left,           y_center,            x_left + l, y_center}                  // G (中)
  };

  const char* onmap[] = {
    "ABCDEF",  //0
    "BC",      //1
    "ABDEG",   //2
    "ABCDG",   //3
    "FBCG",    //4
    "AFGCD",   //5
    "AFGECD",  //6
    "ABC",     //7
    "ABCDEFG", //8
    "ABCDFG"   //9
  };

  bool on[7] = {0};
  for (const char* p = onmap[digit]; *p; ++p) {
    if (*p == ' ') continue;
    int idx = (*p == 'A') ? 0 : (*p == 'B') ? 1 : (*p == 'C') ? 2 :
              (*p == 'D') ? 3 : (*p == 'E') ? 4 : (*p == 'F') ? 5 : 6;
    on[idx] = true;
  }

  for (int i = 0; i < 7; i++) {
    uint32_t c = on[i] ? col : COLOR_OFF;

    // 横線
    if (segs[i].y1 == segs[i].y2) {
      int x1 = (int)segs[i].x1;
      int x2 = (int)segs[i].x2;
      if (x2 < x1) { int tmp = x1; x1 = x2; x2 = tmp; }
      int y = (int)(segs[i].y1 - t * 0.5f);
      M5.Display.fillRect(x1, y, x2 - x1, (int)t, c);
    }
    // 縦線
    else {
      int y1 = (int)segs[i].y1;
      int y2 = (int)segs[i].y2;
      if (y1 < y2) { int tmp = y1; y1 = y2; y2 = tmp; } // y1 を上側に
      int xr = (int)(segs[i].x1 - t * 0.5f);
      M5.Display.fillRect(xr, y2, (int)t, y1 - y2, c);
    }
  }
}

/* ================ Speedエリアだけ再描画（数字を上へ少し移動） ================ */
void drawSpeed(int spd) {
  const float s = SEG_SIZE;
  const float l = 3.0f * s;
  const float h = 5.0f * s;
  const float t = SEG_THICK;
  const float cy = SPEED_CY + SPEED_Y_OFF;   // 上へオフセット

  // クリア（3桁分）
  const float x_left  = SPEED_CX0 - l * 0.5f - 2;
  const float x_right = SPEED_CX0 + 2 * SEG_SPACING + l * 0.5f + 2;
  const float y_top   = cy - h * 0.5f - t - 2;
  const float y_bot   = cy + h * 0.5f + t + 2;
  M5.Display.fillRect((int)x_left, (int)y_top, (int)(x_right - x_left), (int)(y_bot - y_top), COLOR_BLACK);

  // 桁分解
  if (spd < 0) spd = 0;
  if (spd > 999) spd = 999;
  int d1 = (spd / 100) % 10;
  int d2 = (spd / 10)  % 10;
  int d3 = (spd      ) % 10;

  // 描画（ライム）
  draw7Seg(SPEED_CX0 + 0 * SEG_SPACING, cy, SEG_SIZE, d1, COLOR_LIME);
  draw7Seg(SPEED_CX0 + 1 * SEG_SPACING, cy, SEG_SIZE, d2, COLOR_LIME);
  draw7Seg(SPEED_CX0 + 2 * SEG_SPACING, cy, SEG_SIZE, d3, COLOR_LIME);
}

/* ================ 固定UI描画（F/E・km/h・初期速度） ================ */
void drawStaticUI() {
  M5.Display.fillScreen(COLOR_BLACK);

  // Fuel labels（F/E を上へ）
  M5.Display.setTextColor(COLOR_WHITE, COLOR_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(baseX, baseY - 20 + (int)TEXT_Y_OFF);
  M5.Display.print("F");
  int totalH = FUEL_BLOCKS * blockH + (FUEL_BLOCKS - 1) * gap;
  M5.Display.setCursor(baseX, baseY + totalH + 5 + (int)TEXT_Y_OFF);
  M5.Display.print("E");

  // 初期速度（0から開始）
  drawSpeed(g_speed);

  // "km/h"（上へオフセット）
  M5.Display.setTextColor(COLOR_WHITE, COLOR_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(190, 90 + (int)TEXT_Y_OFF);
  M5.Display.print("km/h");
}

/* ================ Fuel gauge（上から消える） ================ */
void drawFuelGauge() {
  int totalH = FUEL_BLOCKS * blockH + (FUEL_BLOCKS - 1) * gap;
  int y0 = baseY + (int)GAUGE_Y_OFF;

  // 領域クリア
  M5.Display.fillRect(baseX, y0, blockW, totalH, COLOR_BLACK);

  // 下から積む（残量ぶん）
  for (int i = 0; i < fuelBlocks; i++) {
    int by = y0 + (FUEL_BLOCKS - 1 - i) * (blockH + gap);
    uint32_t col = (fuelBlocks == 1 && i == 0) ? COLOR_RED : COLOR_ORANGE;
    M5.Display.fillRect(baseX, by, blockW, blockH, col);
  }
}

/* ================ ACC数値表示（roll_deg*roll_sign） ================ */
void drawAccValue(float roll_value_deg) {
  int x = ACC_X;
  int y = ACC_Y + (int)TEXT_Y_OFF;      // 全体と同じ上方オフセット
  M5.Display.fillRect(x - 2, y - 2, 120, 20, COLOR_BLACK);

  M5.Display.setTextColor(COLOR_WHITE, COLOR_BLACK);
  M5.Display.setTextSize(2);
  M5.Display.setCursor(x, y);
  M5.Display.printf("%5.1f", roll_value_deg);
}

/* ================ Unity 受信（SPD:123 をパース） ================ */
void processUnitySerialBT() {
  static char buf[32];
  static int idx = 0;

  while (SerialBT.available()) {            // ★ Serial → SerialBT
    char ch = (char)SerialBT.read();
    if (ch == '\r') continue;
    if (ch == '\n') {
      buf[idx] = '\0';
      idx = 0;

      // 形式: SPD:<int>
      if (strncmp(buf, "SPD:", 4) == 0) {
        int v = atoi(buf + 4);
        v = constrain(v, 0, 999);
        if (v != g_speed) {
          g_speed = v;
          drawSpeed(g_speed);   // すぐ画面更新
        }
      }
      continue;
    }
    if (idx < (int)sizeof(buf) - 1) buf[idx++] = ch;
  }
}

/* ===================== setup ===================== */
void setup() {
  auto cfg = M5.config();
  M5.begin(cfg);
  M5.Imu.begin();

  Serial.begin(115200);             // デバッグ用（任意）
  SerialBT.begin("M5Speedo");       // ★ BTデバイス名（Windowsでペアリング時に表示）
  delay(200);

  // ボタン（内部プルアップ）
  pinMode(BTN_PIN,  INPUT_PULLUP);  // G26
  pinMode(BTN2_PIN, INPUT_PULLUP);  // G0  ※起動中は押さない

  // ADC設定（12bit, 〜3.3Vレンジ）
  analogReadResolution(12);                    // 0..4095
  analogSetPinAttenuation(36 /*POT_PIN*/, ADC_11db);

  // 起動時水平でゼロ点合わせ
  float s = 0.0f;
  for (int i = 0; i < 200; ++i) {
    float ax, ay, az;
    M5.Imu.getAccel(&ax, &ay, &az);
    float roll_acc = atan2f(ay, az) * 180.0f / PI;
    s += roll_acc;
    delay(5);
  }
  zero_offset = s / 200.0f;

  last_ms = millis();

  // 画面設定＆色
  M5.Display.setRotation(3);
  M5.Display.setBrightness(255);
  M5.Display.setColorDepth(24);
  M5.Display.invertDisplay(false);

  COLOR_LIME   = M5.Display.color888(0x32, 0xCD, 0x32);
  COLOR_ORANGE = M5.Display.color888(0xFF, 0xA5, 0x00);
  COLOR_RED    = M5.Display.color888(0xFF, 0x3B, 0x30);
  COLOR_WHITE  = M5.Display.color888(0xFF, 0xFF, 0xFF);
  COLOR_BLACK  = M5.Display.color888(0x00, 0x00, 0x00);
  COLOR_OFF    = M5.Display.color888(25, 25, 25);

  drawStaticUI();
  drawFuelGauge();
  drawAccValue(0.0f);  // 初期表示

  lastFuelTick = millis();
}

/* ===================== loop ===================== */
void loop() {
  M5.update();
  uint32_t now = millis();

  /* ---- Unity からの速度受信（Bluetooth SPP, 非ブロッキング） ---- */
  processUnitySerialBT();

  /* ---- ロール角（相補フィルタ） ---- */
  float ax, ay, az, gx, gy, gz;
  M5.Imu.getAccel(&ax, &ay, &az);
  M5.Imu.getGyro(&gx, &gy, &gz);

  float dt = (now - last_ms) / 1000.0f;
  last_ms = now;

  float roll_acc = atan2f(ay, az) * 180.0f / PI - zero_offset;

  static float roll = 0.0f;
  const float alpha = 0.98f;                    // 0.95〜0.99で調整可
  roll = alpha * (roll + gx * dt) + (1.0f - alpha) * roll_acc;

  // 表示安定用ローパス
  roll_deg = 0.8f * roll_deg + 0.2f * roll;

  // Aボタンでゼロリセット
  if (M5.BtnA.wasPressed()) {
    zero_offset += roll_deg;
    roll = 0.0f;
    roll_deg = 0.0f;
  }

  /* ---- ブレーキ（G26）デボウンス ---- */
  bool nowSw = digitalRead(BTN_PIN);  // HIGH=離し, LOW=押し
  if (nowSw != swLastRead) { swLastChg = now; swLastRead = nowSw; }
  if ((now - swLastChg) > DEBOUNCE_MS && swLastStable != swLastRead) {
    swLastStable = swLastRead;
    bool pressed = (swLastStable == LOW);
    sendUnityLine(String("BTN:") + (pressed ? "1" : "0"));
  }

  /* ---- 追加ボタン（G0）デボウンス ---- */
  bool nowSw2 = digitalRead(BTN2_PIN);  // HIGH=離し, LOW=押し
  if (nowSw2 != sw2LastRead) { sw2LastChg = now; sw2LastRead = nowSw2; }
  if ((now - sw2LastChg) > DEBOUNCE_MS && sw2LastStable != sw2LastRead) {
    sw2LastStable = sw2LastRead;
    bool pressed2 = (sw2LastStable == LOW);
    sendUnityLine(String("BTN2:") + (pressed2 ? "1" : "0"));
  }

  /* ---- アクセル（G36）0..1にマップ → 反転 ---- */
  int raw = analogRead(36); // POT_PIN
  if (raw < RAW_MIN) raw = RAW_MIN;
  if (raw > RAW_MAX) raw = RAW_MAX;

  float thr = (raw - RAW_MIN) / float(RAW_MAX - RAW_MIN);  // 0..1
  static float thr_f = 0.0f;
  thr_f = thr_f * 0.85f + thr * 0.15f;  // 軽いローパス
  float thr_out = INVERT_THROTTLE ? (1.0f - thr_f) : thr_f;

  /* ---- UnityへBluetoothで送信 ----
     1) ステア（roll）: 数値のみ（右マイナス/左プラス）
     2) アクセル       : "THR:0.000"
     3) BTN/BTN2 は変化時のみ送信
  ------------------------------------------------ */
  sendUnityLine(String(roll_deg * roll_sign, 2));               // 例: "-12.34"
  sendUnityLine(String("THR:") + String(thr_out, 3));           // 例: "THR:0.537"

  /* ---- 画面更新 ---- */
  // accの場所に roll（deg, 1桁）を表示
  drawAccValue(roll_deg * roll_sign);

  // 燃料：1分ごとに1ブロック減（上から）
  if (now - lastFuelTick >= FUEL_INTERVAL_MS) {
    lastFuelTick = now;
    if (fuelBlocks > 0) {
      fuelBlocks--;
      drawFuelGauge();
    }
  }

  delay(10);
}
