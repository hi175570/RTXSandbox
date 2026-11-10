using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.UI;

namespace RTXSandbox.ManifoldGardenBridge.Plugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ManifoldGardenBridgePlugin : BasePlugin
{
    public const string PluginGuid = "rtxsandbox.manifoldgarden.bridge";
    public const string PluginName = "RTX Sandbox Manifold Garden Bridge";
    public const string PluginVersion = "0.4.0";

    private Harmony? _harmony;

    public override void Load()
    {
        int targetFrameRate = Config.Bind("Performance", "TargetFrameRate", 60,
            "Limits Manifold Garden while RTX Sandbox renders the shared block layer.").Value;
        float blockSize = Config.Bind("World", "BlockSize", 1f,
            "World-space edge length of RTX bridge blocks.").Value;
        float walkSpeed = Config.Bind("Player", "WalkSpeed", 5.2f,
            "RTX Sandbox walking speed used by the Manifold player body.").Value;
        float runSpeed = Config.Bind("Player", "RunSpeed", 8.5f,
            "RTX Sandbox running speed used by the Manifold player body.").Value;
        float jumpSpeed = Config.Bind("Player", "JumpSpeed", 7.2f,
            "RTX Sandbox jump velocity along the current Manifold up direction.").Value;
        bool showComposite = Config.Bind("Interface", "ShowRtxComposite", true,
            "Composites the real RTX Sandbox framebuffer and HUD over Manifold Garden.").Value;

        BridgePump.Initialize(Log, targetFrameRate, blockSize, walkSpeed, runSpeed, jumpSpeed, showComposite);
        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(ManifoldGardenBridgePlugin).Assembly);
        Log.LogInfo($"Bridge 0.4 online; controls: RMB place, LMB break, E Manifold interact; map={BridgeProtocol.MapName}");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        BridgePump.Dispose();
        return true;
    }
}

[HarmonyPatch(typeof(global::GameManager), nameof(global::GameManager.LateUpdate))]
internal static class GameManagerLateUpdatePatch
{
    [HarmonyPostfix]
    private static void Postfix() => BridgePump.Tick();
}

[HarmonyPatch(typeof(global::InputController), nameof(global::InputController.GetButtonDown), typeof(string))]
internal static class BridgeGetButtonDownPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string key, ref bool __result) => BridgePump.FilterButton(key, 0, ref __result);
}

[HarmonyPatch(typeof(global::InputController), nameof(global::InputController.GetButton), typeof(string))]
internal static class BridgeGetButtonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string key, ref bool __result) => BridgePump.FilterButton(key, 1, ref __result);
}

[HarmonyPatch(typeof(global::InputController), nameof(global::InputController.GetButtonUp), typeof(string))]
internal static class BridgeGetButtonUpPatch
{
    [HarmonyPrefix]
    private static bool Prefix(string key, ref bool __result) => BridgePump.FilterButton(key, 2, ref __result);
}

internal static class BridgePump
{
    private const string ProxyPrefix = "RTXBridgeCollision_";
    private static readonly Dictionary<(int X, int Y, int Z), GameObject> CollisionProxies = new();
    private static readonly ushort[] ConsumerHotbar = new ushort[BridgeProtocol.HotbarSlotCount];

    private static BridgeWriter? _writer;
    private static BridgeFrameReader? _frameReader;
    private static ManualLogSource? _log;
    private static int _processId;
    private static int _connectedConsumerProcessId;
    private static int _lastFrameLogged;
    private static long _lastAcknowledgedPlacementLogged;
    private static ushort _selectedBlockId = 1;
    private static int _selectedHotbarSlot;
    private static float _blockSize = 1f;
    private static float _walkSpeed = 5.2f;
    private static float _runSpeed = 8.5f;
    private static float _jumpSpeed = 7.2f;
    private static bool _showComposite = true;
    private static bool _hasGridOrigin;
    private static Vector3 _gridOrigin;
    private static Quaternion _gridRotation = Quaternion.identity;
    private static DateTime _nextFrameOpenUtc;
    private static byte[] _framePixels = Array.Empty<byte>();
    private static long _lastFrameNumber;
    private static GameObject? _compositeRoot;
    private static RawImage? _compositeImage;
    private static Texture2D? _compositeTexture;

    public static bool Active => _writer != null;

    public static void Initialize(
        ManualLogSource log,
        int targetFrameRate,
        float blockSize,
        float walkSpeed,
        float runSpeed,
        float jumpSpeed,
        bool showComposite)
    {
        _log = log;
        _processId = Process.GetCurrentProcess().Id;
        _writer = new BridgeWriter();
        _blockSize = Mathf.Clamp(blockSize, 0.1f, 10f);
        _walkSpeed = Mathf.Max(0.1f, walkSpeed);
        _runSpeed = Mathf.Max(_walkSpeed, runSpeed);
        _jumpSpeed = Mathf.Max(0.1f, jumpSpeed);
        _showComposite = showComposite;
        if (targetFrameRate > 0) Application.targetFrameRate = targetFrameRate;
    }

    public static bool FilterButton(string key, int phase, ref bool result)
    {
        if (!Active) return true;
        if (key == global::InputController.gp_changeGravity)
        {
            result = false;
            return false;
        }
        if (key == global::InputController.gp_Interact)
        {
            result = phase switch
            {
                0 => Input.GetKeyDown(KeyCode.E),
                1 => Input.GetKey(KeyCode.E),
                _ => Input.GetKeyUp(KeyCode.E),
            };
            return false;
        }
        return true;
    }

    public static void Tick()
    {
        BridgeWriter? writer = _writer;
        if (writer is null) return;

        ApplyRtxPlayerMovement();
        Camera? camera = global::GameManager.MainCam;
        bool hasCamera = camera != null;
        Vector3 position = hasCamera ? camera!.transform.position : Vector3.zero;
        Quaternion rotation = hasCamera ? camera!.transform.rotation : Quaternion.identity;
        int frame = Time.frameCount;

        writer.Write(new BridgeSnapshot(
            _processId, DateTime.UtcNow.Ticks, frame, hasCamera,
            position.x, position.y, position.z,
            rotation.x, rotation.y, rotation.z, rotation.w,
            hasCamera ? camera!.fieldOfView : 0f,
            hasCamera ? camera!.pixelWidth : Screen.width,
            hasCamera ? camera!.pixelHeight : Screen.height,
            0));

        bool consumerIsFresh = writer.TryReadConsumer(
            out int consumerProcessId, out _, out DateTime consumerUtc, out ushort selectedBlockId) &&
            DateTime.UtcNow - consumerUtc <= TimeSpan.FromSeconds(10);
        if (consumerIsFresh)
        {
            _selectedBlockId = selectedBlockId == 0 ? (ushort)1 : selectedBlockId;
            writer.ReadConsumerHotbar(ConsumerHotbar, out _selectedHotbarSlot);
            if (hasCamera && Input.GetMouseButtonDown(1)) TryPlaceBlock(writer, camera!);
            if (hasCamera && Input.GetMouseButtonDown(0)) TryRemoveBlock(writer, camera!);
        }

        if (_showComposite) UpdateComposite();

        if (frame % 30 == 0)
        {
            if (consumerIsFresh && consumerProcessId != _connectedConsumerProcessId)
            {
                _connectedConsumerProcessId = consumerProcessId;
                _log?.LogInfo($"RTX Sandbox consumer connected: pid={consumerProcessId}");
            }
            else if (!consumerIsFresh && _connectedConsumerProcessId != 0)
            {
                _log?.LogInfo($"RTX Sandbox consumer disconnected: pid={_connectedConsumerProcessId}");
                _connectedConsumerProcessId = 0;
            }
            long acknowledged = writer.ReadAcknowledgedPlacementSequence();
            if (acknowledged > _lastAcknowledgedPlacementLogged)
            {
                _lastAcknowledgedPlacementLogged = acknowledged;
                _log?.LogInfo($"RTX Sandbox committed block action sequence {acknowledged}.");
            }
        }

        if (frame - _lastFrameLogged >= 600)
        {
            _lastFrameLogged = frame;
            _log?.LogDebug($"Heartbeat frame={frame}, camera={hasCamera}, position=({position.x:F2}, {position.y:F2}, {position.z:F2})");
        }
    }

    private static void ApplyRtxPlayerMovement()
    {
        global::RigidbodyController? controller = global::GameManager.PlayerController;
        if (controller == null) return;
        controller.walkSpeed = _walkSpeed;
        controller.runSpeed = _runSpeed;
        if (!Input.GetKeyDown(KeyCode.Space) || !controller.isGrounded) return;

        Rigidbody body = controller.Rigidbody;
        Vector3 up = controller.BodyUp.normalized;
        body.velocity = Vector3.ProjectOnPlane(body.velocity, up) + up * _jumpSpeed;
    }

    private static void TryPlaceBlock(BridgeWriter writer, Camera camera)
    {
        Ray ray = new(camera.transform.position, camera.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
        {
            _log?.LogWarning("Placement ignored: the crosshair did not hit a collidable surface.");
            return;
        }

        Vector3 normal = hit.normal.normalized;
        (int X, int Y, int Z) grid;
        if (TryParseProxy(hit.collider.gameObject.name, out (int X, int Y, int Z) hitGrid))
        {
            Vector3Int step = DominantAxis(Quaternion.Inverse(_gridRotation) * normal);
            grid = (hitGrid.X + step.x, hitGrid.Y + step.y, hitGrid.Z + step.z);
        }
        else
        {
            Vector3 rawCenter = hit.point + normal * (_blockSize * 0.501f);
            if (!_hasGridOrigin)
            {
                _gridOrigin = rawCenter;
                _gridRotation = Quaternion.FromToRotation(Vector3.up, normal);
                _hasGridOrigin = true;
                grid = (0, 0, 0);
            }
            else
            {
                Vector3 local = Quaternion.Inverse(_gridRotation) * (rawCenter - _gridOrigin);
                grid = (
                    Mathf.RoundToInt(local.x / _blockSize),
                    Mathf.RoundToInt(local.y / _blockSize),
                    Mathf.RoundToInt(local.z / _blockSize));
            }
        }

        if (CollisionProxies.ContainsKey(grid)) return;
        Vector3 worldCenter = GridToWorld(grid);
        GameObject proxy = new($"{ProxyPrefix}{grid.X}_{grid.Y}_{grid.Z}");
        proxy.transform.position = worldCenter;
        proxy.transform.rotation = _gridRotation;
        BoxCollider collider = proxy.AddComponent<BoxCollider>();
        collider.size = Vector3.one * _blockSize;
        CollisionProxies.Add(grid, proxy);

        long sequence = writer.WritePlacement(
            grid.X, grid.Y, grid.Z,
            worldCenter.x, worldCenter.y, worldCenter.z,
            _gridRotation.x, _gridRotation.y, _gridRotation.z, _gridRotation.w,
            normal.x, normal.y, normal.z,
            _selectedBlockId, BridgeBlockAction.Place, _blockSize);
        _log?.LogInfo($"RTX place #{_selectedBlockId} cell=({grid.X},{grid.Y},{grid.Z}) seq={sequence}");
    }

    private static void TryRemoveBlock(BridgeWriter writer, Camera camera)
    {
        Ray ray = new(camera.transform.position, camera.transform.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore) ||
            !TryParseProxy(hit.collider.gameObject.name, out (int X, int Y, int Z) grid) ||
            !CollisionProxies.TryGetValue(grid, out GameObject? proxy))
            return;

        Vector3 center = proxy.transform.position;
        CollisionProxies.Remove(grid);
        UnityEngine.Object.Destroy(proxy);
        long sequence = writer.WritePlacement(
            grid.X, grid.Y, grid.Z,
            center.x, center.y, center.z,
            _gridRotation.x, _gridRotation.y, _gridRotation.z, _gridRotation.w,
            hit.normal.x, hit.normal.y, hit.normal.z,
            0, BridgeBlockAction.Remove, _blockSize);
        _log?.LogInfo($"RTX remove cell=({grid.X},{grid.Y},{grid.Z}) seq={sequence}");
    }

    private static Vector3 GridToWorld((int X, int Y, int Z) grid) =>
        _gridOrigin + _gridRotation * new Vector3(
            grid.X * _blockSize, grid.Y * _blockSize, grid.Z * _blockSize);

    private static void UpdateComposite()
    {
        if (_frameReader is null && DateTime.UtcNow >= _nextFrameOpenUtc)
        {
            _nextFrameOpenUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            try { BridgeFrameReader.TryOpen(out _frameReader); }
            catch (Exception exception) { _log?.LogDebug($"RTX frame map not ready: {exception.Message}"); }
        }
        if (_frameReader is null ||
            !_frameReader.TryRead(ref _framePixels, out int width, out int height, out long frameNumber) ||
            frameNumber == _lastFrameNumber)
            return;

        _lastFrameNumber = frameNumber;
        EnsureComposite(width, height);
        if (_compositeTexture == null) return;
        GCHandle handle = GCHandle.Alloc(_framePixels, GCHandleType.Pinned);
        try
        {
            _compositeTexture.LoadRawTextureData(handle.AddrOfPinnedObject(), _framePixels.Length);
            _compositeTexture.Apply(false, false);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void EnsureComposite(int width, int height)
    {
        if (_compositeRoot == null)
        {
            _compositeRoot = new GameObject("RTXSandboxFramebufferComposite");
            UnityEngine.Object.DontDestroyOnLoad(_compositeRoot);
            Canvas canvas = _compositeRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            GameObject imageObject = CreateUiObject("RTXFramebuffer", _compositeRoot.transform);
            RectTransform rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _compositeImage = imageObject.AddComponent<RawImage>();
            _compositeImage.raycastTarget = false;
            _compositeImage.color = Color.white;
            _compositeImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        if (_compositeTexture != null &&
            _compositeTexture.width == width && _compositeTexture.height == height)
            return;
        if (_compositeTexture != null) UnityEngine.Object.Destroy(_compositeTexture);
        _compositeTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        if (_compositeImage != null) _compositeImage.texture = _compositeTexture;
        _log?.LogInfo($"RTX framebuffer composite ready: {width}x{height}");
    }

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        Il2CppReferenceArray<Il2CppSystem.Type> components = new(1);
#pragma warning disable CS0618
        components[0] = RectTransform.Il2CppType;
#pragma warning restore CS0618
        GameObject gameObject = new(name, components);
        gameObject.transform.SetParent(parent, false);
        return gameObject;
    }

    private static Vector3Int DominantAxis(Vector3 normal)
    {
        float x = Mathf.Abs(normal.x), y = Mathf.Abs(normal.y), z = Mathf.Abs(normal.z);
        if (x >= y && x >= z) return new Vector3Int(normal.x >= 0f ? 1 : -1, 0, 0);
        if (y >= z) return new Vector3Int(0, normal.y >= 0f ? 1 : -1, 0);
        return new Vector3Int(0, 0, normal.z >= 0f ? 1 : -1);
    }

    private static bool TryParseProxy(string name, out (int X, int Y, int Z) grid)
    {
        grid = default;
        if (!name.StartsWith(ProxyPrefix, StringComparison.Ordinal)) return false;
        string[] parts = name.Substring(ProxyPrefix.Length).Split('_');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int z)) return false;
        grid = (x, y, z);
        return true;
    }

    public static void Dispose()
    {
        foreach (GameObject proxy in CollisionProxies.Values)
            if (proxy != null) UnityEngine.Object.Destroy(proxy);
        CollisionProxies.Clear();
        if (_compositeTexture != null) UnityEngine.Object.Destroy(_compositeTexture);
        if (_compositeRoot != null) UnityEngine.Object.Destroy(_compositeRoot);
        _compositeTexture = null;
        _compositeImage = null;
        _compositeRoot = null;
        _frameReader?.Dispose();
        _frameReader = null;
        _writer?.Dispose();
        _writer = null;
        _framePixels = Array.Empty<byte>();
        _lastFrameNumber = 0;
        _hasGridOrigin = false;
        _connectedConsumerProcessId = 0;
        _lastAcknowledgedPlacementLogged = 0;
        Array.Clear(ConsumerHotbar, 0, ConsumerHotbar.Length);
    }
}
