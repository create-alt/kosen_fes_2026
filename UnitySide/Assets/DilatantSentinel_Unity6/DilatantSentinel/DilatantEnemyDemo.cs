using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class DilatantEnemyDemo : MonoBehaviour
{
    [SerializeField] private DilatantEnemy target;
    [SerializeField] private bool showControls = true;

    private void Update()
    {
        if (target == null) return;

#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) target.ReceiveWeakAttack();
        if (keyboard.digit2Key.wasPressedThisFrame) target.ReceiveStrongAttack();
        if (keyboard.rKey.wasPressedThisFrame) target.ResetEnemy();
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Alpha1)) target.ReceiveWeakAttack();
        if (Input.GetKeyDown(KeyCode.Alpha2)) target.ReceiveStrongAttack();
        if (Input.GetKeyDown(KeyCode.R)) target.ResetEnemy();
#endif
    }

    private void OnGUI()
    {
        if (!showControls || target == null) return;
        GUI.Box(new Rect(16f, 16f, 320f, 90f), "Dilatant Sentinel");
        GUI.Label(new Rect(28f, 42f, 300f, 24f), "HP: " + target.CurrentHealth.ToString("0") + " / " + target.MaxHealth.ToString("0"));
        GUI.Label(new Rect(28f, 66f, 300f, 24f), "[1] Weak    [2] Strong    [R] Reset");
    }
}
