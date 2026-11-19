using System.Numerics;
using CaixaMaker.App.Models;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.App.Viewport3D;

/// <summary>Snapshot imutável dos vetores de câmera para projeção perspectiva 3D e picking.</summary>
public readonly record struct CameraFrame(
    Vector3 Eye,
    Vector3 Target,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float ViewportWidth,
    float ViewportHeight,
    float FocalLengthPx)
{
    public bool TryProject(Vector3 world, out Vector2 screenPos, out float zCam)
    {
        Vector3 rel = world - Eye;
        zCam = Vector3.Dot(rel, Forward);
        if (zCam <= 0.5f)
        {
            screenPos = default;
            return false;
        }

        float xCam = Vector3.Dot(rel, Right);
        float yCam = Vector3.Dot(rel, Up);
        float sx = ViewportWidth * 0.5f + (xCam / zCam) * FocalLengthPx;
        float sy = ViewportHeight * 0.5f - (yCam / zCam) * FocalLengthPx;
        screenPos = new Vector2(sx, sy);
        return true;
    }

    public Vector3 ProjectWithDepth(Vector3 world)
    {
        Vector3 rel = world - Eye;
        float zCam = MathF.Max(0.5f, Vector3.Dot(rel, Forward));
        float xCam = Vector3.Dot(rel, Right);
        float yCam = Vector3.Dot(rel, Up);
        float sx = ViewportWidth * 0.5f + (xCam / zCam) * FocalLengthPx;
        float sy = ViewportHeight * 0.5f - (yCam / zCam) * FocalLengthPx;
        return new Vector3(sx, sy, zCam);
    }

    public Vector2 ProjectDirection(Vector3 unitDir)
    {
        float xCam = Vector3.Dot(unitDir, Right);
        float yCam = Vector3.Dot(unitDir, Up);
        return new Vector2(xCam, -yCam);
    }

    public float ProjectDirectionDepth(Vector3 unitDir) => Vector3.Dot(unitDir, Forward);
}

/// <summary>
/// Controlador orbital e ortográfico 3D desacoplado de UI (sincronizado com <see cref="BoxWorkspaceViewModel"/>),
/// com suporte a órbita, pan em espaço da câmera, zoom com limites seguros, 6 presets e ray/triangle picking.
/// </summary>
public sealed class Camera3DController
{
    public const float FovYDeg = 38f;
    public const double MinZoom = 0.35;
    public const double MaxZoom = 6.0;
    public const double MinPitchDeg = -85.0;
    public const double MaxPitchDeg = 88.0;

    public CameraPreset ActivePreset { get; private set; } = CameraPreset.Isometric;
    public double YawDeg { get; private set; } = 38.0;
    public double PitchDeg { get; private set; } = 26.0;
    public double Zoom { get; private set; } = 1.0;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public void SyncFromViewModel(BoxWorkspaceViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ActivePreset = vm.ActiveCameraPreset;
        YawDeg = vm.CameraYawDeg;
        PitchDeg = Math.Clamp(vm.CameraPitchDeg, MinPitchDeg, MaxPitchDeg);
        Zoom = Math.Clamp(vm.CameraZoom, MinZoom, MaxZoom);
        PanX = vm.CameraPanX;
        PanY = vm.CameraPanY;
    }

    public void SyncToViewModel(BoxWorkspaceViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.ActiveCameraPreset = ActivePreset;
        vm.CameraYawDeg = YawDeg;
        vm.CameraPitchDeg = PitchDeg;
        vm.CameraZoom = Zoom;
        vm.CameraPanX = PanX;
        vm.CameraPanY = PanY;
    }

    public void ApplyPreset(CameraPreset preset)
    {
        ActivePreset = preset;
        PanX = 0.0;
        PanY = 0.0;
        (YawDeg, PitchDeg) = preset switch
        {
            CameraPreset.Front => (0.0, 6.0),
            CameraPreset.Back => (180.0, 6.0),
            CameraPreset.Left => (-90.0, 6.0),
            CameraPreset.Right => (90.0, 6.0),
            CameraPreset.Top => (0.0, 86.0),
            _ => (38.0, 26.0),
        };
    }

    public void Reset()
    {
        Zoom = 1.0;
        ApplyPreset(CameraPreset.Isometric);
    }

    public void Orbit(double deltaPxX, double deltaPxY)
    {
        YawDeg -= deltaPxX * 0.55;
        PitchDeg = Math.Clamp(PitchDeg + deltaPxY * 0.5, MinPitchDeg, MaxPitchDeg);
    }

    public void Pan(double deltaPxX, double deltaPxY, double sceneScaleMm = 200.0)
    {
        double mmPerPx = Math.Max(0.15, sceneScaleMm * 0.0022 / Math.Max(0.35, Zoom));
        PanX -= deltaPxX * mmPerPx;
        PanY += deltaPxY * mmPerPx;
    }

    public void ZoomBy(double factor)
    {
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
    }

    public CameraFrame BuildFrame(BoxScene3D scene, float viewportWidth, float viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(scene);

        float w = MathF.Max(1f, viewportWidth);
        float h = MathF.Max(1f, viewportHeight);
        float aspect = w / h;
        float horizontalFit = aspect < 1f ? 1.12f / MathF.Max(aspect, 0.5f) : 1.05f;

        float maxDim = MathF.Max(
            (float)(scene.WidthMm + 2.0 * scene.ThicknessMm),
            MathF.Max((float)(scene.DepthMm + 6.0 * scene.ThicknessMm), (float)scene.HeightMm));
        if (scene.IsExplodedView)
        {
            maxDim *= 1.32f;
        }

        float baseDistance = MathF.Max(210f, maxDim * 2.15f * horizontalFit);
        float dist = baseDistance / (float)Math.Clamp(Zoom, MinZoom, MaxZoom);

        float yawRad = (float)(YawDeg * Math.PI / 180.0);
        float pitchRad = (float)(PitchDeg * Math.PI / 180.0);

        float cosP = MathF.Cos(pitchRad);
        float sinP = MathF.Sin(pitchRad);
        float cosY = MathF.Cos(yawRad);
        float sinY = MathF.Sin(yawRad);

        Vector3 offsetDir = Vector3.Normalize(new Vector3(cosP * sinY, sinP, cosP * cosY));
        Vector3 baseTarget = new(0f, (float)(scene.HeightMm * 0.42), 0f);

        // Calcula eixos antes do pan para aplicar PanX / PanY exatamente no plano da tela
        Vector3 prelimEye = baseTarget + offsetDir * dist;
        Vector3 forward = Vector3.Normalize(baseTarget - prelimEye);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        Vector3 target = baseTarget + right * (float)PanX + up * (float)PanY;
        Vector3 eye = target + offsetDir * dist;

        float focalPx = (h * 0.5f) / MathF.Tan((FovYDeg * MathF.PI / 180f) * 0.5f);
        return new CameraFrame(eye, target, right, up, forward, w, h, focalPx);
    }

    /// <summary>Identifica a chapa 3D mais próxima da câmera sob o ponto <c>(px, py)</c>.</summary>
    public ExtrudedPanel3D? PickPanel(BoxScene3D scene, float px, float py, int viewportWidth, int viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(scene);

        CameraFrame frame = BuildFrame(scene, viewportWidth, viewportHeight);
        ExtrudedPanel3D? bestPanel = null;
        float bestInvZ = float.NegativeInfinity;

        foreach (ExtrudedPanel3D panel in scene.Panels)
        {
            foreach (MeshTriangle3D tri in panel.Triangles)
            {
                if (Vector3.Dot(tri.Normal, frame.Eye - tri.A) <= 0f)
                {
                    continue;
                }

                Vector3 pa = frame.ProjectWithDepth(tri.A);
                Vector3 pb = frame.ProjectWithDepth(tri.B);
                Vector3 pc = frame.ProjectWithDepth(tri.C);

                if (TryPointInProjectedTriangle(
                        px, py,
                        pa, pb, pc,
                        out float invZ) &&
                    invZ > bestInvZ)
                {
                    bestInvZ = invZ;
                    bestPanel = panel;
                }
            }
        }

        return bestPanel;
    }

    private static bool TryPointInProjectedTriangle(
        float px,
        float py,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float invZ)
    {
        float area = EdgeFunction(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (MathF.Abs(area) < 1e-4f)
        {
            invZ = 0f;
            return false;
        }

        float w0 = EdgeFunction(b.X, b.Y, c.X, c.Y, px, py) / area;
        float w1 = EdgeFunction(c.X, c.Y, a.X, a.Y, px, py) / area;
        float w2 = EdgeFunction(a.X, a.Y, b.X, b.Y, px, py) / area;

        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
        {
            invZ = 0f;
            return false;
        }

        invZ = w0 / a.Z + w1 / b.Z + w2 / c.Z;
        return true;
    }

    private static float EdgeFunction(float ax, float ay, float bx, float by, float px, float py)
        => (px - ax) * (by - ay) - (py - ay) * (bx - ax);
}
