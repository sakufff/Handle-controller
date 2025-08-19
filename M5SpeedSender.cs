using UnityEngine;

/// �ԑ�(km/h)�� M5 �� SPD:<int> �ő��
[AddComponentMenu("Vehicle Physics/Utility/M5 Speed Sender")]
public class M5SpeedSender : MonoBehaviour
{
    [Header("Refs")]
    public M5StickSerialSteering m5; // M5Input �I�u�W�F�N�g���h���b�O
    public Rigidbody targetRb;       // �ԗ��� Rigidbody�i�����擾�j

    [Header("Send Settings")]
    [Tooltip("���M�Ԋu[�b]�i0.1 = 10Hz�j")]
    public float sendInterval = 0.10f;
    [Tooltip("�l���ς�����Ƃ��������M")]
    public bool onlyWhenChanged = true;
    [Tooltip("�����؂�̂Ăł͂Ȃ��l�̌ܓ�")]
    public bool roundToNearest = true;

    float _timer;
    int _lastSent = -9999;

    void Reset()
    {
        if (m5 == null) m5 = FindObjectOfType<M5StickSerialSteering>();
        if (targetRb == null)
        {
            // �߂��� Rigidbody ��T��
            targetRb = GetComponentInParent<Rigidbody>();
            if (targetRb == null) targetRb = FindObjectOfType<Rigidbody>();
        }
    }

    void Update()
    {
        if (m5 == null || !m5.IsPortOpen) return;
        if (targetRb == null) return;

        _timer += Time.deltaTime;
        if (_timer < sendInterval) return;
        _timer = 0f;

        float kmh = targetRb.linearVelocity.magnitude * 3.6f;
        int spd = roundToNearest ? Mathf.Clamp(Mathf.RoundToInt(kmh), 0, 999)
                                 : Mathf.Clamp((int)kmh, 0, 999);

        if (onlyWhenChanged && spd == _lastSent) return;

        m5.SendLine($"SPD:{spd}");
        _lastSent = spd;
    }
}
