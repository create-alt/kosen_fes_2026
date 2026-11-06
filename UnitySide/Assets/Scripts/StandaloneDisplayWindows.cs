using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class StandaloneDisplayWindows : MonoBehaviour
{
    [SerializeField] private bool openExtraWindows = true;
    [SerializeField, Min(320)] private int windowWidth = 960;
    [SerializeField, Min(240)] private int windowHeight = 540;
    [SerializeField, Range(10, 60)] private int mirrorFrameRate = 30;
    [SerializeField] private bool flipDisplay1Vertically = false;
    [SerializeField] private bool flipDisplay4Vertically = false;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private static StandaloneDisplayWindows instance;
    private readonly List<CameraState> cameras = new List<CameraState>();
    private readonly List<CanvasState> canvases = new List<CanvasState>();
    private NativeWindow mainMirror;
    private NativeWindow fourthWindow;
    private RenderTexture mainTexture;
    private RenderTexture fourthTexture;
    private bool mainPending;
    private bool fourthPending;
    private bool closing;
    private bool readbackErrorReported;
    private bool previousRunInBackground;

    private struct CameraState
    {
        public Camera camera;
        public RenderTexture texture;
    }

    private struct CanvasState
    {
        public Canvas canvas;
        public RenderMode mode;
        public Camera camera;
        public float distance;
    }

    private IEnumerator Start()
    {
        if (!openExtraWindows || instance != null) yield break;
        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            Debug.LogError("別ウィンドウ表示にはAsync GPU Readback対応のGPUが必要です。", this);
            yield break;
        }

        instance = this;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
        yield return new WaitForEndOfFrame();
        if (closing) yield break;

        try
        {
            mainMirror = new NativeWindow("FightEngineering - Display 1 (Mirror)", windowWidth, windowHeight);
            fourthWindow = new NativeWindow("FightEngineering - Display 4", windowWidth, windowHeight);
            fourthTexture = CreateTexture(windowWidth, windowHeight, 24);
            BindFourthDisplay();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            enabled = false;
            yield break;
        }

        WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
        float nextFrame = 0f;
        while (!closing)
        {
            yield return endOfFrame;
            if (closing || Time.unscaledTime < nextFrame) continue;
            nextFrame = Time.unscaledTime + 1f / Mathf.Clamp(mirrorFrameRate, 10, 60);

            if (mainMirror.IsOpen && !mainPending && Screen.width > 0 && Screen.height > 0)
            {
                if (mainTexture == null || mainTexture.width != Screen.width || mainTexture.height != Screen.height)
                {
                    ReleaseTexture(mainTexture);
                    mainTexture = CreateTexture(Screen.width, Screen.height, 0);
                }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(mainTexture);
                mainPending = true;
                AsyncGPUReadback.Request(mainTexture, 0, TextureFormat.RGBA32, ReadMain);
            }
            if (fourthWindow.IsOpen && !fourthPending)
            {
                fourthPending = true;
                AsyncGPUReadback.Request(fourthTexture, 0, TextureFormat.RGBA32, ReadFourth);
            }
        }
    }

    private void BindFourthDisplay()
    {
        Camera uiCamera = null;
        foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (camera.targetDisplay != 3 || !camera.enabled) continue;
            cameras.Add(new CameraState { camera = camera, texture = camera.targetTexture });
            camera.targetTexture = fourthTexture;
            if (uiCamera == null || camera.depth > uiCamera.depth) uiCamera = camera;
        }
        if (uiCamera == null)
        {
            throw new InvalidOperationException("Display 4を出力する有効なCameraがありません。Sub CameraのTarget DisplayをDisplay 4に設定してください。");
        }
        foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!canvas.isRootCanvas || canvas.targetDisplay != 3 || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            canvases.Add(new CanvasState { canvas = canvas, mode = canvas.renderMode, camera = canvas.worldCamera, distance = canvas.planeDistance });
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCamera;
            canvas.planeDistance = uiCamera.nearClipPlane + Mathf.Min(0.1f, (uiCamera.farClipPlane - uiCamera.nearClipPlane) * 0.01f);
        }
    }

    private static RenderTexture CreateTexture(int width, int height, int depth)
    {
        RenderTexture texture = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32);
        texture.name = "Standalone Window Frame";
        texture.Create();
        return texture;
    }

    private void ReadMain(AsyncGPUReadbackRequest request)
    {
        mainPending = false;
        if (closing || ReadbackFailed(request)) return;
        mainMirror.SetFrame(request, mainTexture.width, mainTexture.height, flipDisplay1Vertically);
    }

    private void ReadFourth(AsyncGPUReadbackRequest request)
    {
        fourthPending = false;
        if (closing || ReadbackFailed(request)) return;
        fourthWindow.SetFrame(request, fourthTexture.width, fourthTexture.height, flipDisplay4Vertically);
    }

    private bool ReadbackFailed(AsyncGPUReadbackRequest request)
    {
        if (!request.hasError) return false;
        if (!readbackErrorReported) Debug.LogError("別ウィンドウ用の映像を取得できません。WindowsビルドのGraphics APIとGPU対応状況を確認してください。", this);
        readbackErrorReported = true;
        return true;
    }

    private void Update()
    {
        mainMirror?.PumpMessages();
        fourthWindow?.PumpMessages();
    }

    private void OnDisable()
    {
        if (instance != this) return;
        closing = true;
        StopAllCoroutines();
        if (mainPending || fourthPending) AsyncGPUReadback.WaitAllRequests();
        foreach (CanvasState state in canvases)
        {
            if (state.canvas == null) continue;
            state.canvas.renderMode = state.mode;
            state.canvas.worldCamera = state.camera;
            state.canvas.planeDistance = state.distance;
        }
        foreach (CameraState state in cameras)
        {
            if (state.camera != null) state.camera.targetTexture = state.texture;
        }
        mainMirror?.Dispose();
        fourthWindow?.Dispose();
        ReleaseTexture(mainTexture);
        ReleaseTexture(fourthTexture);
        Application.runInBackground = previousRunInBackground;
        instance = null;
    }

    private static void ReleaseTexture(RenderTexture texture)
    {
        if (texture == null) return;
        texture.Release();
        Destroy(texture);
    }

    private sealed class NativeWindow : IDisposable
    {
        private const string ClassName = "FightEngineeringDisplayWindow";
        private static readonly Dictionary<IntPtr, NativeWindow> windows = new Dictionary<IntPtr, NativeWindow>();
        private static readonly WindowProcedure procedure = ProcessMessage;
        private static bool classRegistered;
        private IntPtr handle;
        private byte[] pixels;
        private BitmapInfo bitmap;
        public bool IsOpen => handle != IntPtr.Zero;

        public NativeWindow(string title, int width, int height)
        {
            IntPtr module = GetModuleHandle(null);
            if (!classRegistered)
            {
                WindowClass definition = new WindowClass
                {
                    size = (uint)Marshal.SizeOf<WindowClass>(),
                    procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                    instance = module,
                    cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                    background = new IntPtr(5),
                    name = ClassName
                };
                if (RegisterClassEx(ref definition) == 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                classRegistered = true;
            }
            handle = CreateWindowEx(0, ClassName, title, 0x00CF0000, unchecked((int)0x80000000), unchecked((int)0x80000000), width, height, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (!IsOpen) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            windows.Add(handle, this);
            ShowWindow(handle, 4);
        }

        public void SetFrame(AsyncGPUReadbackRequest request, int width, int height, bool flip)
        {
            if (!IsOpen) return;
            int length = width * height * 4;
            if (pixels == null || pixels.Length != length) pixels = new byte[length];
            request.GetData<byte>().CopyTo(pixels);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte red = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = red;
            }
            bool topDown = SystemInfo.graphicsUVStartsAtTop != flip;
            bitmap = new BitmapInfo { size = 40, width = width, height = topDown ? -height : height, planes = 1, bits = 32 };
            InvalidateRect(handle, IntPtr.Zero, false);
        }

        public void PumpMessages()
        {
            if (!IsOpen) return;
            while (PeekMessage(out Message message, handle, 0, 0, 1))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
                if (!IsOpen) break;
            }
        }

        private void Paint()
        {
            IntPtr dc = BeginPaint(handle, out PaintState state);
            try
            {
                GetClientRect(handle, out Rectangle rectangle);
                FillRect(dc, ref rectangle, GetStockObject(4));
                if (pixels == null || rectangle.right <= 0 || rectangle.bottom <= 0) return;
                int height = Math.Abs(bitmap.height);
                float scale = Math.Min(rectangle.right / (float)bitmap.width, rectangle.bottom / (float)height);
                int widthOut = (int)(bitmap.width * scale);
                int heightOut = (int)(height * scale);
                StretchDIBits(dc, (rectangle.right - widthOut) / 2, (rectangle.bottom - heightOut) / 2, widthOut, heightOut, 0, 0, bitmap.width, height, pixels, ref bitmap, 0, 0x00CC0020);
            }
            finally
            {
                EndPaint(handle, ref state);
            }
        }

        [AOT.MonoPInvokeCallback(typeof(WindowProcedure))]
        private static IntPtr ProcessMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (windows.TryGetValue(window, out NativeWindow view))
            {
                if (message == 0x000F)
                {
                    view.Paint();
                    return IntPtr.Zero;
                }
                if (message == 0x0014) return new IntPtr(1);
                if (message == 0x0010)
                {
                    view.Dispose();
                    return IntPtr.Zero;
                }
            }
            return DefWindowProc(window, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (!IsOpen) return;
            IntPtr previous = handle;
            windows.Remove(previous);
            handle = IntPtr.Zero;
            DestroyWindow(previous);
            pixels = null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint size, style;
            public IntPtr procedure;
            public int classExtra, windowExtra;
            public IntPtr instance, icon, cursor, background;
            public string menu, name;
            public IntPtr smallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rectangle { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr window;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x, y;
            public uint privateValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaintState
        {
            public IntPtr dc;
            public int erase;
            public Rectangle paint;
            public int restore, update;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint size;
            public int width, height;
            public ushort planes, bits;
            public uint compression, imageSize;
            public int xPixels, yPixels;
            public uint colorsUsed, importantColors, color;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extra, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintState state);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintState state);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rectangle rectangle);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Rectangle rectangle, IntPtr brush);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY, int sourceWidth, int sourceHeight, byte[] bits, ref BitmapInfo info, uint usage, uint operation);
    }
#endif
}
