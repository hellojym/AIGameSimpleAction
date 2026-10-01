using UnityEngine;

namespace ActionTest
{
    /// <summary>
    /// 斜俯视轨道相机（Orbit Camera Rig）。
    ///
    /// 设计：控制器和 Camera 挂在同一个 GameObject 上，位置由「观察点 + 球坐标」推导：
    ///     position = target - (Euler(pitch, yaw, 0) * forward) * distance
    /// 不依赖父子层级，所以场景里只有一台相机，没有隐藏的空节点。
    ///
    /// 操作：
    ///   左键拖拽      旋转（绕观察点公转）
    ///   右键 / 中键拖拽 平移
    ///   滚轮 / 触控板  缩放（指数步进，默认朝光标指向的地面点缩放）
    ///   WASD / 方向键  平移，Q/E 升降观察点，F 回到初始位姿
    ///   触屏：单指旋转、双指平移、捏合缩放
    ///
    /// 注意：用的是旧版 Input（Input Manager）。如果之后装了 Input System 包并把
    /// 「Active Input Handling」切成 Input System Only，这里要跟着改。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ActionTest/Orbit Camera Rig (斜俯视轨道相机)")]
    public class OrbitCameraRig : MonoBehaviour
    {
        [Header("观察点")]
        [Tooltip("相机环绕的中心点（世界坐标）")]
        public Vector3 target = Vector3.zero;

        [Header("初始位姿")]
        [Tooltip("水平环绕角（度）")]
        public float yaw = 35f;

        [Tooltip("俯仰角（度）：90 = 正俯视，越小越接近平视")]
        public float pitch = 45f;

        [Tooltip("相机到观察点的距离")]
        public float distance = 18f;

        [Header("旋转")]
        [Tooltip("按住哪个键拖拽旋转：0=左键 1=右键 2=中键")]
        public int rotateButton = 0;

        [Tooltip("拖动一个像素转多少度")]
        public float rotateSpeed = 0.28f;

        [Tooltip("旋转阻尼时间：0 = 完全不缓动")]
        public float rotateSmoothTime = 0.06f;

        [Tooltip("勾上 = 「抓住场景拖」的手感（默认）；不勾 = 相机朝拖动方向走，手感相反")]
        public bool grabStyleOrbit = true;

        [Tooltip("俯仰角下限，别让相机钻到地面以下")]
        public float minPitch = 3f;

        [Tooltip("俯仰角上限")]
        public float maxPitch = 89f;

        [Header("缩放")]
        [Tooltip("最近能拉到多近（0.2 = 几乎贴在地面上看）")]
        public float minDistance = 0.2f;

        [Tooltip("最远距离")]
        public float maxDistance = 300f;

        [Tooltip("滚轮灵敏度：每格的距离倍率 = exp(-zoomSpeed)")]
        public float zoomSpeed = 0.15f;

        [Tooltip("缩放阻尼时间")]
        public float zoomSmoothTime = 0.08f;

        [Tooltip("朝光标指向的地面点缩放（而不是朝屏幕中心缩放）")]
        public bool zoomToCursor = true;

        [Header("平移")]
        public bool panWithRightButton = true;
        public bool panWithMiddleButton = true;

        [Tooltip("拖拽平移的速度倍率：1 = 拖动一屏，观察点移动约一个「可见高度」")]
        public float panSpeed = 1f;

        [Tooltip("平移阻尼时间")]
        public float panSmoothTime = 0.06f;

        public bool keyboardPan = true;

        [Tooltip("键盘平移速度（米/秒），会随距离放大到当前视野尺度")]
        public float keyboardPanSpeed = 12f;

        [Tooltip("勾上 = 观察点可以无限移动；不勾 = 限制在 panLimit 的 XZ 范围内")]
        public bool unlimitedPan = true;

        [Tooltip("unlimitedPan = false 时生效：观察点 XZ 活动半径")]
        public Vector2 panLimit = new Vector2(200f, 200f);

        [Header("其它")]
        [Tooltip("用 unscaledDeltaTime：Time.timeScale = 0 时相机依然可操作")]
        public bool useUnscaledTime = true;

        [Tooltip("整体缓动总开关")]
        public bool smooth = true;

        [Tooltip("按 F 回到初始位姿")]
        public bool resetKey = true;

        [Tooltip("触屏：单指旋转 / 双指平移 / 捏合缩放")]
        public bool touchSupport = true;

        // ── 运行时状态 ────────────────────────────────────────────
        Camera _cam;

        // 「初始位姿」：序列化保存，按 F / ResetView 回到这里。
        // 不写成"启用时临时记一下"——编辑器里组件不一定走 OnEnable，
        // 那样会导致第一次按 F 记录的是"已经被鼠标挪过"的位姿（实测踩到）。
        [SerializeField, HideInInspector] bool _startCaptured;
        [SerializeField, HideInInspector] Vector3 _startTarget;
        [SerializeField, HideInInspector] float _startYaw;
        [SerializeField, HideInInspector] float _startPitch;
        [SerializeField, HideInInspector] float _startDistance;

        // 平滑后的当前值（相机实际用的是这一组）
        Vector3 _sTarget;
        float _sYaw, _sPitch, _sDistance;
        Vector3 _targetVel;
        float _yawVel, _pitchVel, _distVel;

        // 自算鼠标增量（不用 GetAxis("Mouse X")，避免灵敏度缩放与丢帧问题）
        Vector3 _lastMouse;
        bool _hadMouseButton;

        /// <summary>当前平滑后的距离，方便外部脚本（例如 LOD、裁剪）读取。</summary>
        public float CurrentDistance { get { return _sDistance; } }

        /// <summary>当前平滑后的观察点。</summary>
        public Vector3 CurrentTarget { get { return _sTarget; } }

        Camera Cam
        {
            get
            {
                if (_cam == null) _cam = GetComponent<Camera>();
                return _cam;
            }
        }

        void OnEnable()
        {
            if (!_startCaptured) CaptureStartPose();
            ApplyImmediate();
        }

        void OnValidate()
        {
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            minDistance = Mathf.Max(0.01f, minDistance);
            maxDistance = Mathf.Max(minDistance + 0.01f, maxDistance);
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
            if (!Application.isPlaying) ApplyImmediate();
        }

        void Update()
        {
            float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            if (dt <= 0f) dt = 0.0166f;

            UpdateInput(dt);
            UpdateSmoothing(dt);
            ApplyTransform(_sTarget, _sYaw, _sPitch, _sDistance);
        }

        /// <summary>把平滑状态直接钉到目标值并刷新相机 Transform（编辑器里调参用）。</summary>
        public void ApplyImmediate()
        {
            _sTarget = target;
            _sYaw = yaw;
            _sPitch = pitch;
            _sDistance = distance;
            _targetVel = Vector3.zero;
            _yawVel = _pitchVel = _distVel = 0f;
            ApplyTransform(_sTarget, _sYaw, _sPitch, _sDistance);
        }

        /// <summary>把当前位姿记为「初始位姿」（F 键 / ResetView 的归位目标）。</summary>
        [ContextMenu("把当前位姿记录为初始位姿")]
        public void CaptureStartPose()
        {
            _startCaptured = true;
            _startTarget = target;
            _startYaw = yaw;
            _startPitch = pitch;
            _startDistance = distance;
        }

        /// <summary>回到初始位姿（按 F 或代码调用）。</summary>
        public void ResetView()
        {
            if (!_startCaptured) CaptureStartPose();
            target = _startTarget;
            yaw = _startYaw;
            pitch = _startPitch;
            distance = _startDistance;
        }

        /// <summary>把观察点挪到某个世界坐标（保持当前角度与距离）。</summary>
        public void Focus(Vector3 worldPoint)
        {
            target = worldPoint;
        }

        // ── 输入 ─────────────────────────────────────────────────
        void UpdateInput(float dt)
        {
            if (touchSupport && Input.touchCount > 0)
            {
                UpdateTouch();
                return;
            }

            UpdateMouse();
            UpdateKeyboard(dt);
        }

        void UpdateMouse()
        {
            Vector3 mp = Input.mousePosition;
            bool anyButton = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);

            Vector3 delta = mp - _lastMouse;
            _lastMouse = mp;

            // 刚按下的那一帧不产生增量，避免"一按就跳"
            if (!anyButton || !_hadMouseButton) delta = Vector3.zero;
            _hadMouseButton = anyButton;

            if (delta == Vector3.zero && Mathf.Abs(Input.mouseScrollDelta.y) < 1e-4f) return;

            // 1) 缩放
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 1e-4f) Zoom(scroll * zoomSpeed);

            // 2) 旋转
            if (Input.GetMouseButton(rotateButton))
            {
                float sign = grabStyleOrbit ? -1f : 1f;
                yaw += delta.x * rotateSpeed * sign;
                pitch = Mathf.Clamp(pitch + delta.y * rotateSpeed * sign, minPitch, maxPitch);
            }

            // 3) 平移
            bool panning = (panWithRightButton && Input.GetMouseButton(1))
                           || (panWithMiddleButton && Input.GetMouseButton(2));
            if (panning) PanByScreenDelta(delta.x, delta.y);
        }

        void UpdateKeyboard(float dt)
        {
            if (resetKey && Input.GetKeyDown(KeyCode.F))
            {
                ResetView();
                return;
            }

            if (!keyboardPan) return;

            float h = 0f, v = 0f, up = 0f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v -= 1f;
            if (Input.GetKey(KeyCode.E)) up += 1f;
            if (Input.GetKey(KeyCode.Q)) up -= 1f;

            if (h == 0f && v == 0f && up == 0f) return;

            // 速度随距离放大，远看时不会"推不动"，贴近地面时也不会"一推就飞"
            float speed = keyboardPanSpeed * Mathf.Clamp(_sDistance / 18f, 0.15f, 6f);
            Quaternion flat = Quaternion.Euler(0f, _sYaw, 0f);
            Vector3 right = flat * Vector3.right;
            Vector3 fwd = flat * Vector3.forward;

            Vector3 move = (right * h + fwd * v) * speed * dt;
            move.y += up * speed * dt;
            target = ClampTarget(target + move);
        }

        void UpdateTouch()
        {
            if (Input.touchCount == 1)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == TouchPhase.Moved)
                {
                    float sign = grabStyleOrbit ? -1f : 1f;
                    yaw += t.deltaPosition.x * rotateSpeed * sign;
                    pitch = Mathf.Clamp(pitch - t.deltaPosition.y * rotateSpeed * sign, minPitch, maxPitch);
                }
                return;
            }

            Touch a = Input.GetTouch(0);
            Touch b = Input.GetTouch(1);

            Vector2 aPrev = a.position - a.deltaPosition;
            Vector2 bPrev = b.position - b.deltaPosition;
            float prevSpan = (aPrev - bPrev).magnitude;
            float nowSpan = (a.position - b.position).magnitude;

            if (prevSpan > 1f && nowSpan > 1f)
            {
                // 捏合：手指距离变为原来的 k 倍 -> 距离也乘 1/k
                float ratio = prevSpan / nowSpan;
                Zoom(Mathf.Log(ratio));
            }

            Vector2 midDelta = (a.deltaPosition + b.deltaPosition) * 0.5f;
            if (midDelta.sqrMagnitude > 0f) PanByScreenDelta(-midDelta.x, midDelta.y);
        }

        // ── 操作实现 ──────────────────────────────────────────────
        /// <summary>指数缩放；zoomToCursor 时同步挪动观察点，让光标下的地面点保持不动。</summary>
        public void Zoom(float amount)
        {
            if (Mathf.Approximately(amount, 0f)) return;

            float newDistance = Mathf.Clamp(distance * Mathf.Exp(-amount), minDistance, maxDistance);
            if (Mathf.Approximately(newDistance, distance)) return;

            if (zoomToCursor && Cam != null)
            {
                Quaternion rot = Quaternion.Euler(_sPitch, _sYaw, 0f);
                Vector3 fwd = rot * Vector3.forward;
                float planeY = _sTarget.y;

                Vector3 dir;
                if (TryCursorRay(rot, out dir) && Mathf.Abs(dir.y) > 0.08f)
                {
                    Vector3 originBefore = target - fwd * distance;
                    Vector3 originAfter = target - fwd * newDistance;
                    Vector3 anchorBefore = IntersectPlane(originBefore, dir, planeY);
                    Vector3 anchorAfter = IntersectPlane(originAfter, dir, planeY);
                    target = ClampTarget(target + (anchorBefore - anchorAfter));
                }
            }

            distance = newDistance;
        }

        /// <summary>按屏幕像素增量平移观察点（右/上为正方向）。</summary>
        void PanByScreenDelta(float dxPixels, float dyPixels)
        {
            if (Cam == null) return;

            float viewHeight = 2f * _sDistance * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float perPixel = viewHeight / Mathf.Max(1f, Screen.height) * panSpeed;

            Quaternion flat = Quaternion.Euler(0f, _sYaw, 0f);
            Vector3 move = (flat * Vector3.right) * -dxPixels + (flat * Vector3.forward) * -dyPixels;
            target = ClampTarget(target + move * perPixel);
        }

        /// <summary>光标在屏幕上的视线方向（相机机体 x/y/z 直接按 fov 展开，不依赖 transform 的当前状态）。</summary>
        bool TryCursorRay(Quaternion rot, out Vector3 dir)
        {
            dir = Vector3.forward;
            if (Cam == null) return false;

            float vHalf = Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = Cam.aspect > 0f ? Cam.aspect : 16f / 9f;
            Vector3 mp = Input.mousePosition;

            float u = mp.x / Mathf.Max(1f, Screen.width) * 2f - 1f;
            float v = mp.y / Mathf.Max(1f, Screen.height) * 2f - 1f;

            Vector3 dirCam = new Vector3(u * vHalf * aspect, v * vHalf, 1f);
            dir = (rot * dirCam).normalized;
            return true;
        }

        static Vector3 IntersectPlane(Vector3 origin, Vector3 dir, float planeY)
        {
            if (Mathf.Abs(dir.y) < 1e-4f) return origin;
            float t = (planeY - origin.y) / dir.y;
            return origin + dir * t;
        }

        Vector3 ClampTarget(Vector3 v)
        {
            if (unlimitedPan) return v;
            return new Vector3(Mathf.Clamp(v.x, -panLimit.x, panLimit.x), v.y,
                               Mathf.Clamp(v.z, -panLimit.y, panLimit.y));
        }

        // ── 平滑与落地 ────────────────────────────────────────────
        void UpdateSmoothing(float dt)
        {
            if (!smooth)
            {
                _sTarget = target;
                _sYaw = yaw;
                _sPitch = pitch;
                _sDistance = distance;
                return;
            }

            _sTarget = Vector3.SmoothDamp(_sTarget, target, ref _targetVel, panSmoothTime, Mathf.Infinity, dt);
            _sYaw = Mathf.SmoothDampAngle(_sYaw, yaw, ref _yawVel, rotateSmoothTime, Mathf.Infinity, dt);
            _sPitch = Mathf.SmoothDampAngle(_sPitch, pitch, ref _pitchVel, rotateSmoothTime, Mathf.Infinity, dt);
            _sDistance = Mathf.SmoothDamp(_sDistance, distance, ref _distVel, zoomSmoothTime, Mathf.Infinity, dt);
        }

        void ApplyTransform(Vector3 t, float y, float p, float d)
        {
            Quaternion rot = Quaternion.Euler(p, y, 0f);
            transform.rotation = rot;
            transform.position = t - rot * Vector3.forward * d;

            // 拉到很近时收紧近裁剪面，避免贴着地面看时被裁掉
            if (Cam != null) Cam.nearClipPlane = Mathf.Clamp(d * 0.02f, 0.01f, 0.5f);
        }
    }
}
