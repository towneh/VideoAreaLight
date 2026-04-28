using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DisallowMultipleComponent]
#if VAL_HAS_CILBOX && !VAL_DISABLE_CILBOX
[Cilboxable]
#endif
public class VideoAreaLightSource : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Drag your VideoPlayer's render texture in here. Drives the light's colour and reflection image.")]
    public Texture videoTexture;

    [Header("Screen")]
    [Tooltip("Half the screen's local size. Leave at (0.5, 0.5) for a default Quad. For a stretched 16:9 quad, divide one axis by the aspect ratio.")]
    public Vector2 localHalfSize = new Vector2(0.5f, 0.5f);
    [Tooltip("Which axes the screen faces along. XY for a Quad mesh, XZ for a Plane mesh.")]
    public Axis screenAxis = Axis.XY;
    public enum Axis { XY, XZ }
    [Tooltip("Lights both sides of the screen. Leave off for a normal one-sided panel — it's faster and physically correct.")]
    public bool twoSided = false;
    [Tooltip("Flips which side the light shines from. Unity's default Quad usually needs this ON.")]
    public bool flipNormal = false;

    [Header("Sampling")]
    [Tooltip("How often (per second) the video's average colour is read. 15 is plenty; raising it just costs more.")]
    [Range(1f, 60f)] public float sampleRate = 15f;
    [Tooltip("How quickly the light reacts to colour changes. 0 is snappy, higher is smoother.")]
    [Range(0f, 1f)] public float responseTime = 0.15f;

    [Header("Intensity")]
    [Tooltip("Light brightness when the video is fully white. Biggest lever — try 30–100 for clubs, more if you're using bloom.")]
    public float maxIntensity = 4f;
    [Tooltip("Light brightness when the video is fully black. Leave at 0 for a natural fade-out.")]
    public float minIntensity = 0f;
    [Tooltip("Shapes the brightness response. Above 1 darkens midtones for punchier contrast; below 1 lifts them.")]
    [Range(0.25f, 4f)] public float intensityCurve = 1.5f;
    [Tooltip("Boosts the colour saturation of the light. 1 leaves it as-is.")]
    [Range(1f, 3f)] public float saturationBoost = 1.4f;

    [Header("Zone Mask")]
    [Tooltip("Drag a BoxCollider here to bound the screen's lighting to your venue. Anything outside the box stays dark — the cheapest way to stop reflections leaking into other rooms. The collider's rotation and scale are respected, so you can tilt the box to fit angled rooms.")]
    public BoxCollider zoneVolume;
    [Tooltip("Softens the edge of the Zone Mask. 0 is a hard cut at the wall; around 0.1 gives a subtle fade.")]
    [Range(0f, 2f)] public float zoneFeather = 0.1f;

    static int _PositionsId;
    static int _ColorId;
    static int _IntensityId;
    static int _NormalId;
    static int _CookieTexId;
    static int _CookieMipCountId;
    static int _TwoSidedId;
    static int _ValidId;
    static int _CookieMatrixId;
    static int _ZoneEnabledId;
    static int _WorldToZoneId;
    static int _ZoneHalfExtentsId;
    static int _ZoneFeatherId;
    static bool _idsCached;

    readonly Vector4[] _corners = new Vector4[4];

    RenderTexture _avgRT;
    Color _currentAvg = Color.black;
    Color _targetAvg  = Color.black;
    float _lastSampleTime;
    AsyncGPUReadbackRequest? _pendingReq;

    void OnEnable()
    {
        EnsureIds();
        EnsureRT();
    }
    void OnDisable()
    {
        Cleanup();
        _pendingReq = null;
        Shader.SetGlobalFloat(_ValidId, 0f);
    }

    void Cleanup()
    {
        if (_avgRT != null) { _avgRT.Release(); DestroyImmediate(_avgRT); _avgRT = null; }
    }

    void EnsureIds()
    {
        if (_idsCached) return;
        _PositionsId       = Shader.PropertyToID("_VAL_Positions");
        _ColorId           = Shader.PropertyToID("_VAL_Color");
        _IntensityId       = Shader.PropertyToID("_VAL_Intensity");
        _NormalId          = Shader.PropertyToID("_VAL_Normal");
        _CookieTexId       = Shader.PropertyToID("_VAL_CookieTex");
        _CookieMipCountId  = Shader.PropertyToID("_VAL_CookieMipCount");
        _TwoSidedId        = Shader.PropertyToID("_VAL_TwoSided");
        _ValidId           = Shader.PropertyToID("_VAL_Valid");
        _CookieMatrixId    = Shader.PropertyToID("_VAL_CookieWorldToUV");
        _ZoneEnabledId     = Shader.PropertyToID("_VAL_ZoneEnabled");
        _WorldToZoneId     = Shader.PropertyToID("_VAL_WorldToZone");
        _ZoneHalfExtentsId = Shader.PropertyToID("_VAL_ZoneHalfExtents");
        _ZoneFeatherId     = Shader.PropertyToID("_VAL_ZoneFeather");
        _idsCached = true;
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
        EnsureIds();
        EnsureRT();

        if (_pendingReq.HasValue && _pendingReq.Value.done)
        {
            var req = _pendingReq.Value;
            _pendingReq = null;
            if (!req.hasError)
            {
                var data = req.GetData<Color32>();
                if (data.Length >= 16)
                {
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
            }
        }

        if (videoTexture != null)
        {
            float interval = 1f / Mathf.Max(sampleRate, 0.01f);
            if (!_pendingReq.HasValue && Time.realtimeSinceStartup - _lastSampleTime >= interval)
            {
                _lastSampleTime = Time.realtimeSinceStartup;
                Graphics.Blit(videoTexture, _avgRT);
                _pendingReq = AsyncGPUReadback.Request(_avgRT, 0);
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

        if (videoTexture != null)
        {
            Shader.SetGlobalTexture(_CookieTexId, videoTexture);
            // mipmapCount drives the shader's LOD ramp. Textures without
            // mipmaps report 1, collapsing the ramp to mip 0.
            Shader.SetGlobalFloat(_CookieMipCountId, Mathf.Max(videoTexture.mipmapCount, 1));
        }

        PushZoneGlobals();

        Shader.SetGlobalFloat(_ValidId, 1f);
    }

    void PushZoneGlobals()
    {
        if (zoneVolume == null)
        {
            Shader.SetGlobalFloat(_ZoneEnabledId, 0f);
            return;
        }

        // worldToLocalMatrix already undoes the collider transform's position,
        // rotation and lossyScale, so a world point lands in the collider's
        // local space where BoxCollider.size is the box extent. Translate by
        // -center to recenter on the actual box pivot — then the test reduces
        // to abs(localPos) < halfExtents.
        Matrix4x4 worldToZone = Matrix4x4.Translate(-zoneVolume.center)
                              * zoneVolume.transform.worldToLocalMatrix;
        Vector3 halfExtents = 0.5f * zoneVolume.size;

        Shader.SetGlobalFloat(_ZoneEnabledId, 1f);
        Shader.SetGlobalMatrix(_WorldToZoneId, worldToZone);
        Shader.SetGlobalVector(_ZoneHalfExtentsId, halfExtents);
        Shader.SetGlobalFloat(_ZoneFeatherId, zoneFeather);
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

        if (zoneVolume != null)
        {
            Gizmos.matrix = zoneVolume.transform.localToWorldMatrix;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireCube(zoneVolume.center, zoneVolume.size);
            Gizmos.matrix = prev;
        }
    }
}
