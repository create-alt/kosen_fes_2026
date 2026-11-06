using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;

public sealed class StandaloneWindowInputModule : StandaloneInputModule
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private WindowMouseInput windowMouse;

    public void Configure(Func<Vector2?> pointerPosition)
    {
        windowMouse = gameObject.AddComponent<WindowMouseInput>();
        windowMouse.PointerPosition = pointerPosition;
        inputOverride = windowMouse;
    }

    public override bool ShouldActivateModule()
    {
        return isActiveAndEnabled && windowMouse != null;
    }

    public override void UpdateModule()
    {
        if (windowMouse != null) windowMouse.Refresh();
    }

    public override void Process()
    {
        if (windowMouse == null) return;
        windowMouse.Refresh();
        bool used = SendUpdateEventToSelectedObject();
        if (eventSystem.sendNavigationEvents)
        {
            if (!used) used = SendMoveEventToSelectedObject();
            if (!used) SendSubmitEventToSelectedObject();
        }
        ProcessMouseEvent();
    }

    protected override void OnDestroy()
    {
        if (windowMouse != null) Destroy(windowMouse);
        base.OnDestroy();
    }
#endif
}

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
internal sealed class WindowMouseInput : BaseInput
{
    public Func<Vector2?> PointerPosition;
    private readonly bool[] held = new bool[3];
    private readonly bool[] down = new bool[3];
    private readonly bool[] up = new bool[3];
    private readonly bool[] previousPhysical = new bool[3];
    private static readonly int[] ButtonKeys = { 1, 2, 4 };
    private Vector2 position = new Vector2(-10000f, -10000f);
    private int sampledFrame = -1;

    public override bool mousePresent => true;
    public override bool touchSupported => false;
    public override int touchCount => 0;
    public override Vector2 mousePosition => position;
    public override Vector2 mouseScrollDelta => Input.mouseScrollDelta;
    public override bool GetMouseButton(int button) => button >= 0 && button < 3 && held[button];
    public override bool GetMouseButtonDown(int button) => button >= 0 && button < 3 && down[button];
    public override bool GetMouseButtonUp(int button) => button >= 0 && button < 3 && up[button];

    public void Refresh()
    {
        if (sampledFrame == Time.frameCount) return;
        sampledFrame = Time.frameCount;
        Vector2? pointer = PointerPosition?.Invoke();
        position = pointer ?? new Vector2(-10000f, -10000f);
        for (int i = 0; i < held.Length; i++)
        {
            bool physical = (GetAsyncKeyState(ButtonKeys[i]) & 0x8000) != 0;
            down[i] = physical && !previousPhysical[i] && pointer.HasValue;
            up[i] = !physical && held[i];
            held[i] = physical && (held[i] || down[i]);
            previousPhysical[i] = physical;
        }
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
#endif
