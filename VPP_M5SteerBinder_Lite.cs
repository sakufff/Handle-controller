using UnityEngine;
using VehiclePhysics;   // VPStandardInput

/// M5StickSerialSteering → VPP Standard Input 橋渡し（軽量版）
[DisallowMultipleComponent]
[AddComponentMenu("Vehicle Physics/Utility/VPP M5 Steer Binder (Lite)")]
public class VPP_M5SteerBinder_Lite : MonoBehaviour
{
    [Header("Refs")]
    public VPStandardInput standardInput;        // 車側の Standard Input
    public M5StickSerialSteering source;         // M5Stick 読み取り 

    [Header("Steer options")]
    public bool invertSteer = false;
    [Range(0.1f, 3f)] public float steerGain = 1f;
    [Range(0f, 0.3f)] public float steerDeadzone = 0f;

    // 自動割り当て（任意）
    void Reset()
    {
#if UNITY_2022_2_OR_NEWER
        if (standardInput == null) standardInput = FindAnyObjectByType<VPStandardInput>();
        if (source == null) source = FindAnyObjectByType<M5StickSerialSteering>();
#else
        if (standardInput == null) standardInput = FindObjectOfType<VPStandardInput>();
        if (source == null)        source        = FindObjectOfType<M5StickSerialSteering>();
#endif
    }

    void Update()
    {
        if (standardInput == null || source == null) return;

        // ---- steer (-1..1)
        float steer = source.steerNormalized;
        if (invertSteer) steer = -steer;
        if (Mathf.Abs(steer) < steerDeadzone) steer = 0f;
        steer = Mathf.Clamp(steer * steerGain, -1f, 1f);

        // ---- throttle 0..1 / brake 0..1（ボタン=ブレーキ）
        float thr = Mathf.Clamp01(source.throttleNormalized);
        float brake = source.buttonPressed ? 1f : 0f;
        if (brake > 0.05f) thr = 0f;            // ブレーキ優先

        // ★ VPStandardInput の正しいプロパティ名（lowerCamelCase）
        standardInput.externalSteer = steer;
        standardInput.externalThrottle = thr;
        standardInput.externalBrake = brake;
        standardInput.reverse = false; // ブレーキでバックしない
    }
}
