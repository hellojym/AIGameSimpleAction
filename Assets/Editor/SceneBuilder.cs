using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ActionTest.EditorTools
{
    /// <summary>
    /// 主场景生成器：地面 + 平行光 + 斜俯视相机，全部用代码建，避免手改 .unity 的 YAML。
    ///
    /// 两个入口：
    ///   · 菜单 ActionTest / 重新生成主场景      —— 人在编辑器里时用
    ///   · SceneBuilder.BuildHeadless()          —— 无头用：-batchmode -executeMethod
    /// 无头入口自己 EditorApplication.Exit(code)，所以 shell 里 echo $? 就能判成败。
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Main.unity";
        public const string MaterialPath = "Assets/Materials/Ground.mat";
        public const string GridTexturePath = "Assets/Textures/Grid.png";
        public const string LogPath = "Logs/scene_build.log";
        public const string PreviewPath = "Logs/preview.png";

        public const string OkMarker = "SCENE_BUILD_OK";
        public const string FailMarker = "SCENE_BUILD_FAIL";

        /// <summary>地面尺寸（米），Plane 图元原始尺寸是 10×10，所以要除以 10。</summary>
        const float GroundSize = 60f;

        /// <summary>贴图一格 = 1 米。</summary>
        const int GridCellsPerTile = 4;
        const int GridTextureSize = 512;

        static StringBuilder _log = new StringBuilder();

        static void Say(string s)
        {
            _log.AppendLine(s);
            Debug.Log("[SceneBuilder] " + s);
        }

        [MenuItem("ActionTest/重新生成主场景 (Main)")]
        public static void BuildFromMenu()
        {
            _log = new StringBuilder();
            try
            {
                Build();
                WriteLog();
                EditorUtility.DisplayDialog("SceneBuilder", "主场景已重新生成：\n" + ScenePath, "好");
            }
            catch (Exception e)
            {
                Say("FAIL: " + e);
                WriteLog();
                EditorUtility.DisplayDialog("SceneBuilder", "生成失败，详见 " + LogPath, "好");
                throw;
            }
        }

        /// <summary>无头入口：失败时以退出码 1 收尾，并把报告写到 Logs/scene_build.log。</summary>
        public static void BuildHeadless()
        {
            _log = new StringBuilder();
            int code = 0;
            try
            {
                Build();
                Say(OkMarker);
            }
            catch (Exception e)
            {
                Say(FailMarker);
                Say(e.ToString());
                code = 1;
            }

            WriteLog();
            EditorApplication.Exit(code);
        }

        /// <summary>渲染一张预览图，用来核对"相机确实斜俯视地面"。</summary>
        public static void PreviewHeadless()
        {
            _log = new StringBuilder();
            int code = 0;
            try
            {
                RenderPreview();
                Say("PREVIEW_OK");
            }
            catch (Exception e)
            {
                Say("PREVIEW_FAIL");
                Say(e.ToString());
                code = 1;
            }

            WriteLog();
            EditorApplication.Exit(code);
        }

        // ── 主体 ─────────────────────────────────────────────────
        public static void Build()
        {
            EnsureFolder("Assets/Scenes");
            EnsureFolder("Assets/Materials");
            EnsureFolder("Assets/Textures");
            EnsureFolder("Logs");

            Texture2D grid = BuildGridTexture();
            Material groundMat = BuildGroundMaterial(grid);

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1) 地面
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.rotation = Quaternion.identity;
            ground.transform.localScale = new Vector3(GroundSize / 10f, 1f, GroundSize / 10f);
            var mr = ground.GetComponent<MeshRenderer>();
            mr.sharedMaterial = groundMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.BatchingStatic);

            // 2) 平行光（场景只有一个光源，带阴影）
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.9725f, 0.9255f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.75f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // 3) 斜俯视相机
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
            camGo.AddComponent<AudioListener>();

            OrbitCameraRig rig = camGo.AddComponent<OrbitCameraRig>();
            rig.target = Vector3.zero;
            rig.yaw = 35f;
            rig.pitch = 45f;
            rig.distance = 18f;
            // 初始位姿写成声明存进场景，保证按 F 归位不依赖 OnEnable 的时序
            rig.CaptureStartPose();
            rig.ApplyImmediate();

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            if (!saved) throw new Exception("保存场景失败：" + ScenePath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 重新从磁盘打开场景做核对：这样验的是"文件里到底存了什么"，
            // 而不是内存里刚创建的那批对象（OpenScene(Single) 会把它们全销毁）。
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var savedCam = Object.FindObjectOfType<Camera>();
            if (savedCam == null) throw new Exception("重新打开场景后找不到 Camera");
            var savedRig = savedCam.GetComponent<OrbitCameraRig>();
            if (savedRig == null) throw new Exception("重新打开场景后 Camera 上没有 OrbitCameraRig");

            Verify();

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Say("场景: " + ScenePath);
            Say(string.Format("相机: yaw={0} pitch={1} distance={2} pos={3}",
                savedRig.yaw, savedRig.pitch, savedRig.distance,
                savedCam.transform.position.ToString("F3")));
            Say("地面: " + GroundSize + "×" + GroundSize + " m");
            Say(string.Format("网格贴图: {0}×{0}, 平铺 {1}×{1}（一格 = 1 米）",
                GridTextureSize, GroundSize / GridCellsPerTile));
        }

        /// <summary>关键约束复核：对象齐全 + 相机确实斜俯视、确实看向地面。</summary>
        static void Verify()
        {
            var cam = Object.FindObjectOfType<Camera>();
            if (cam == null) throw new Exception("场景里没有 Camera");
            var rig = cam.GetComponent<OrbitCameraRig>();
            if (rig == null) throw new Exception("Camera 上没有 OrbitCameraRig");
            if (GameObject.Find("Ground") == null) throw new Exception("场景里没有 Ground");
            if (Object.FindObjectOfType<Light>() == null) throw new Exception("场景里没有 Light");

            // 相机朝向与"从上往下"的夹角
            Vector3 toTarget = (rig.target - cam.transform.position);
            Vector3 fwd = cam.transform.forward;
            float angle = Vector3.Angle(fwd, toTarget);
            float pitch = Vector3.Angle(fwd, Vector3.down);

            Say(string.Format("自检: 相机高度={0:F2} m, 俯视夹角={1:F2}°, 视线与目标方向偏差={2:F3}°",
                cam.transform.position.y, pitch, angle));
            Say(string.Format("自检: 相机与观察点距离={0:F3} m (期望 {1:F3})",
                Vector3.Distance(cam.transform.position, rig.target), rig.distance));

            if (angle > 0.01f) throw new Exception("相机没有看向观察点，偏差 " + angle + "°");
            if (pitch < 20f || pitch > 89f) throw new Exception("俯视角不在 20~89° 之间：" + pitch);
            if (cam.transform.position.y <= 0f) throw new Exception("相机在地面以下");
        }

        // ── 资源 ─────────────────────────────────────────────────
        static Material BuildGroundMaterial(Texture2D grid)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit"); // 万一工程换成 URP
            if (shader == null) throw new Exception("找不到可用的内置着色器（Standard / URP Lit）");

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.shader = shader;
            mat.color = new Color(0.52f, 0.53f, 0.55f, 1f);
            mat.SetFloat("_Glossiness", 0.12f);
            mat.SetFloat("_Metallic", 0f);
            mat.mainTexture = grid;
            // 贴图一格 = 1 米：60 米地面 / 4 格每瓦片 = 15 次平铺
            mat.mainTextureScale = new Vector2(GroundSize / GridCellsPerTile, GroundSize / GridCellsPerTile);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>程序化生成一张可无缝平铺的网格图，方便肉眼确认旋转/缩放/平移真的生效。</summary>
        static Texture2D BuildGridTexture()
        {
            int size = GridTextureSize;
            int cell = size / GridCellsPerTile;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            // 数值偏亮是故意的：贴图会乘到材质 Albedo 上（线性空间），太暗会把地面压成一片黑
            Color32 bg = new Color32(215, 217, 220, 255);
            Color32 line = new Color32(150, 152, 156, 255);
            Color32 major = new Color32(110, 112, 118, 255);

            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool onLine = (x % cell == 0) || (y % cell == 0);
                    bool onMajor = (x == 0) || (y == 0);
                    px[y * size + x] = onMajor ? major : (onLine ? line : bg);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);

            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string abs = Path.Combine(Directory.GetCurrentDirectory(), GridTexturePath);
            File.WriteAllBytes(abs, png);
            AssetDatabase.ImportAsset(GridTexturePath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(GridTexturePath) as TextureImporter;
            if (importer == null) throw new Exception("网格贴图导入失败：" + GridTexturePath);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 9;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();

            var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexturePath);
            if (loaded == null || loaded.width != size)
                throw new Exception("网格贴图读回尺寸不对：" + (loaded == null ? "null" : loaded.width.ToString()));
            return loaded;
        }

        // ── 预览渲染 ─────────────────────────────────────────────
        /// <summary>把场景里的主相机渲到 RenderTexture 再存 PNG。注意要在带图形上下文的情况下跑（别加 -nographics）。</summary>
        public static void RenderPreview()
        {
            EnsureFolder("Logs");
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var cam = Object.FindObjectOfType<Camera>();
            if (cam == null) throw new Exception("场景里没有 Camera");

            const int w = 960, h = 540;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            rt.Create();

            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;

            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);

            string abs = Path.Combine(Directory.GetCurrentDirectory(), PreviewPath);
            File.WriteAllBytes(abs, png);
            Say("预览图: " + abs + " (" + png.Length + " bytes)");
        }

        // ── 工具 ─────────────────────────────────────────────────
        static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static void WriteLog()
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "Logs"));
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), LogPath), _log.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("写日志失败: " + e.Message);
            }
        }
    }
}
