using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DisallowMultipleComponent]
public class VideoAreaLightSource : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The render texture the VideoPlayer writes to. Used as the cookie texture and sampled for average colour.")]
    public Texture videoTexture;

    [Header("Screen Geometry")]
    [Tooltip("Local-space half size of the screen face. For a Unity Quad mesh, leave at (0.5, 0.5). For a stretched 16:9 quad, divide one axis by aspect.")]
    public Vector2 localHalfSize = new Vector2(0.5f, 0.5f);
    [Tooltip("Local axis convention. XY for Unity Quad mesh (default). XZ for Unity Plane mesh.")]
    public Axis screenAxis = Axis.XY;
    public enum Axis { XY, XZ }
    [Tooltip("Two-sided emitter. If false, the area light only emits from the +normal side of the screen face.")]
    public bool twoSided = false;
    [Tooltip("Flip the screen's emit direction. Default convention is transform.forward (XY axis) or transform.up (XZ axis). " +
             "Unity's default Quad mesh has its visible face on the OPPOSITE side, so for a Quad you usually want this ON.")]
    public bool flipNormal = false;

    [Header("Sampling")]
    [Tooltip("Frequency at which the average colour is sampled from the video.")]
    [Range(1f, 60f)] public float sampleRate = 15f;
    [Tooltip("Seconds for the broadcast colour to settle on a new average. 0 = snappy, larger = smoother.")]
    [Range(0f, 1f)] public float responseTime = 0.15f;

    [Header("Intensity")]
    [Tooltip("Light intensity multiplier at full white video.")]
    public float maxIntensity = 4f;
    [Tooltip("Light intensity multiplier at full black video.")]
    public float minIntensity = 0f;
    [Tooltip("Power curve on luminance. >1 darkens midtones, <1 lifts them.")]
    [Range(0.25f, 4f)] public float intensityCurve = 1.5f;
    [Tooltip("Boost saturation of the broadcast colour. 1 = pass-through.")]
    [Range(1f, 3f)] public float saturationBoost = 1.4f;

    static readonly int _PositionsId    = Shader.PropertyToID("_VAL_Positions");
    static readonly int _ColorId        = Shader.PropertyToID("_VAL_Color");
    static readonly int _IntensityId    = Shader.PropertyToID("_VAL_Intensity");
    static readonly int _NormalId       = Shader.PropertyToID("_VAL_Normal");
    static readonly int _CookieTexId    = Shader.PropertyToID("_VAL_CookieTex");
    static readonly int _TwoSidedId     = Shader.PropertyToID("_VAL_TwoSided");
    static readonly int _ValidId        = Shader.PropertyToID("_VAL_Valid");
    static readonly int _CookieMatrixId = Shader.PropertyToID("_VAL_CookieWorldToUV");

    readonly Vector4[] _corners = new Vector4[4];

    RenderTexture _avgRT;
    Color _currentAvg = Color.black;
    Color _targetAvg  = Color.black;
    float _lastSampleTime;
    bool  _readbackInFlight;

    void OnEnable() => EnsureRT();
    void OnDisable()
    {
        Cleanup();
        Shader.SetGlobalFloat(_ValidId, 0f);
    }

    void Cleanup()
    {
        if (_avgRT != null) { _avgRT.Release(); DestroyImmediate(_avgRT); _avgRT = null; }
    }

    void EnsureRT()
    {
        if (_avgRT != null) return;
        _avgRT = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32)
        {
            name = "VideoAreaLight_Avg",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        _avgRT.Create();
    }

    void LateUpdate()
    {
        EnsureRT();

        if (videoTexture != null)
        {
            float interval = 1f / Mathf.Max(sampleRate, 0.01f);
            if (!_readbackInFlight && Time.realtimeSinceStartup - _lastSampleTime >= interval)
            {
                _lastSampleTime = Time.realtimeSinceStartup;
                Graphics.Blit(videoTexture, _avgRT);
                _readbackInFlight = true;
                AsyncGPUReadback.Request(_avgRT, 0, OnAvgReadback);
            }
        }

        float dt = Application.isPlaying ? Time.deltaTime : 1f / 60f;
        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(responseTime, 0.001f));
        _currentAvg = Color.Lerp(_currentAvg, _targetAvg, alpha);

        BuildCorners();
        PushGlobals();
    }

    void BuildCorners()
    {
        Transform t = transform;

        // Local-space corner offsets. TransformPoint() applies position, rotation
        // AND scale, so the resulting world-space corners cover the actual mesh
        // face regardless of how the screen GameObject is scaled.
        Vector3 lBL, lBR, lTR, lTL;
        if (screenAxis == Axis.XY)
        {
            lBL = new Vector3(-localHalfSize.x, -localHalfSize.y, 0f);
            lBR = new Vector3( localHalfSize.x, -localHalfSize.y, 0f);
            lTR = new Vector3( localHalfSize.x,  localHalfSize.y, 0f);
            lTL = new Vector3(-localHalfSize.x,  localHalfSize.y, 0f);
        }
        else
        {
            lBL = new Vector3(-localHalfSize.x, 0f, -localHalfSize.y);
            lBR = new Vector3( localHalfSize.x, 0f, -localHalfSize.y);
            lTR = new Vector3( localHalfSize.x, 0f,  localHalfSize.y);
            lTL = new Vector3(-localHalfSize.x, 0f,  localHalfSize.y);
        }

        Vector3 wBL = t.TransformPoint(lBL);
        Vector3 wBR = t.TransformPoint(lBR);
        Vector3 wTR = t.TransformPoint(lTR);
        Vector3 wTL = t.TransformPoint(lTL);

        // Corners CCW when viewed from the emit direction.
        // Default: viewer is on +transform.forward (XY) / +transform.up (XZ).
        // With flipNormal: viewer is on the opposite side, so we mirror the U axis
        // by swapping P1/P3 to keep CCW order from the new viewpoint.
        if (!flipNormal)
        {
            _corners[0] = wBL;
            _corners[1] = wBR;
            _corners[2] = wTR;
            _corners[3] = wTL;
        }
        else
        {
            _corners[0] = wBR;
            _corners[1] = wBL;
            _corners[2] = wTL;
            _corners[3] = wTR;
        }
    }

    void PushGlobals()
    {
        Color.RGBToHSV(_currentAvg, out float h, out float s, out float _);
        s = Mathf.Clamp01(s * saturationBoost);
        Color tinted = Color.HSVToRGB(h, s, 1f);

        float lum = 0.2126f * _currentAvg.r + 0.7152f * _currentAvg.g + 0.0722f * _currentAvg.b;
        float intensityT = Mathf.Pow(Mathf.Clamp01(lum), intensityCurve);
        float intensity = Mathf.Lerp(minIntensity, maxIntensity, intensityT);

        Vector3 normal = screenAxis == Axis.XY ? transform.forward : transform.up;
        if (flipNormal) normal = -normal;

        Matrix4x4 normalize;
        if (screenAxis == Axis.XY)
        {
            normalize = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f)) *
                        Matrix4x4.Scale(new Vector3(1f / (2f * localHalfSize.x), 1f / (2f * localHalfSize.y), 1f));
        }
        else
        {
            // Map local X -> u, local Z -> v.
            Matrix4x4 swap = Matrix4x4.identity;
            swap.m11 = 0; swap.m12 = 1;
            swap.m21 = 1; swap.m22 = 0;
            normalize = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f)) *
                        Matrix4x4.Scale(new Vector3(1f / (2f * localHalfSize.x), 1f / (2f * localHalfSize.y), 1f)) *
                        swap;
        }
        Matrix4x4 worldToUV = normalize * transform.worldToLocalMatrix;

        Shader.SetGlobalVectorArray(_PositionsId, _corners);
        Shader.SetGlobalColor(_ColorId, tinted);
        Shader.SetGlobalFloat(_IntensityId, intensity);
        Shader.SetGlobalVector(_NormalId, normal);
        Shader.SetGlobalFloat(_TwoSidedId, twoSided ? 1f : 0f);
        Shader.SetGlobalMatrix(_CookieMatrixId, worldToUV);

        if (videoTexture != null) Shader.SetGlobalTexture(_CookieTexId, videoTexture);

        Shader.SetGlobalFloat(_ValidId, 1f);
    }

    void OnAvgReadback(AsyncGPUReadbackRequest req)
    {
        _readbackInFlight = false;
        if (req.hasError) return;
        var data = req.GetData<Color32>();
        if (data.Length < 16) return;

        int sumR = 0, sumG = 0, sumB = 0;
        for (int i = 0; i < 16; i++)
        {
            sumR += data[i].r;
            sumG += data[i].g;
            sumB += data[i].b;
        }
        float div = 16f * 255f;
        _targetAvg = new Color(sumR / div, sumG / div, sumB / div, 1f);
    }

    void OnDrawGizmosSelected()
    {
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Vector3 size = screenAxis == Axis.XY
            ? new Vector3(2f * localHalfSize.x, 2f * localHalfSize.y, 0f)
            : new Vector3(2f * localHalfSize.x, 0f, 2f * localHalfSize.y);
        Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, size);
        Gizmos.matrix = prev;

        Vector3 n = screenAxis == Axis.XY ? transform.forward : transform.up;
        if (flipNormal) n = -n;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + n * 0.5f);
    }
}
