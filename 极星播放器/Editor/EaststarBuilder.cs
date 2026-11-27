using System;
using System.IO;
using System.Collections.Generic;
using VRC.SDKBase.Editor.BuildPipeline;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDK3.Components;
using VRC.SDK3.Video.Components;
using VRC.Udon;

public static class EaststarBuilder
{
    // Resolve paths from the installed builder, not from the extraction folder name.
    private static string Root;
    // Edition preset: baked into the scene/prefab by the Unity editor, never constructed in Udon at runtime.
    private const string EditionName = "普通直连版";
    private static VRC.SDKBase.VRCUrl EditionDefaultUrl() => VRC.SDKBase.VRCUrl.Empty;
    private const string EditionHint = "模式：直接播放";

    [MenuItem("Tools/Eaststar Player/7 - Apply Edition URL Preset")]
    public static void ApplyEditionUrlPreset()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        var selected = Selection.activeGameObject;
        var control = selected && !EditorUtility.IsPersistent(selected) ? selected.GetComponentInParent<EaststarConsole>(true) : null;
        if (!control || PrefabStageUtility.GetCurrentPrefabStage() != null)
        { Debug.LogWarning("[极星播放器] 请在 Hierarchy 选中场景中的控制台。"); return; }
        Undo.RecordObject(control, "Apply Eaststar URL Preset");
        control.defaultInputAddress = EditionDefaultUrl();
        PrefabUtility.RecordPrefabInstancePropertyModifications(control);
        EditorUtility.SetDirty(control);
        EditorSceneManager.MarkSceneDirty(control.gameObject.scene);
        Debug.Log("[极星播放器 v0.3.13] 已应用 " + EditionName + "。请保存场景并重新构建测试。", control.gameObject);
    }
    private static string Generated => Root + "/Generated";
    private static string Prefabs => Root + "/Prefabs";

    internal static bool ResolveRoot()
    {
        Root = null;
        string[] guids = AssetDatabase.FindAssets("EaststarBuilder t:MonoScript", new[] { "Assets" });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            const string suffix = "/Editor/EaststarBuilder.cs";
            if (!path.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string candidate = path.Substring(0, path.Length - suffix.Length);
            if (!AssetDatabase.IsValidFolder(candidate + "/Runtime") ||
                !AssetDatabase.LoadAssetAtPath<MonoScript>(candidate + "/Runtime/EaststarScreen.cs") ||
                !AssetDatabase.LoadAssetAtPath<MonoScript>(candidate + "/Runtime/EaststarConsole.cs") ||
                !AssetDatabase.LoadAssetAtPath<Texture2D>(candidate + "/Textures/Standby.png") ||
                !AssetDatabase.LoadAssetAtPath<Texture2D>(candidate + "/Textures/ConsoleLogo.png") ||
                !AssetDatabase.LoadAssetAtPath<MonoScript>(candidate + "/Runtime/EaststarSecondaryScreen.cs")) continue;
            if (Root != null && Root != candidate)
            {
                Root = null;
                EditorUtility.DisplayDialog("极星播放器", "检测到多套播放器，请只保留一套完整安装。", "确定");
                return false;
            }
            Root = candidate;
        }
        if (Root == null)
        {
            EditorUtility.DisplayDialog("极星播放器", "未找到完整的播放器目录。请确认 Editor、Runtime、Textures 位于同一个播放器文件夹内。", "确定");
            return false;
        }
        Debug.Log("[极星播放器] 实际安装目录：" + Root);
        return true;
    }

    private static void EnsureChildFolder(string name)
    {
        string path = Root + "/" + name;
        if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(Root, name);
        if (!AssetDatabase.IsValidFolder(path)) throw new IOException("无法创建目录：" + path);
    }
    private static Color dark = new Color(.055f,.063f,.075f);
    private static Color panel = new Color(.12f,.14f,.17f);
    private static Color white = new Color(.94f,.95f,.96f);
    private static Color muted = new Color(.65f,.70f,.76f);
    private static Font font;
    private static bool generating;

    [MenuItem("Tools/Eaststar Player/1 - Generate Prefabs")]
    public static void Generate()
    {
        if (generating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        if (!ResolveRoot()) return;
        Scene preview = default(Scene);
        try
        {
            EnsureChildFolder("Generated");
            EnsureChildFolder("Prefabs");
            bool changed = EnsureProgram("EaststarScreen") | EnsureProgram("EaststarConsole") | EnsureProgram("EaststarSecondaryScreen") | EnsureProgram("EaststarDurationProbe");
            if (changed)
            {
                AssetDatabase.SaveAssets();
                UdonSharpProgramAsset.CompileAllCsPrograms(true);
                EditorUtility.DisplayDialog("极星播放器", "已创建程序资源。等待 Unity 编译完成后，再运行一次生成预制件菜单。", "确定");
                return;
            }
            // UdonSharp 1.x serializes proxy fields. Do not force a legacy proxy-to-Udon copy
            // or recompile all programs while creating/saving prefab objects.
            font = FindChineseFont();
            if (!font)
            {
                EditorUtility.DisplayDialog("极星播放器", "请先导入支持简体中文的动态字体（例如 Noto Sans SC），在 Project 中选中字体，再运行生成菜单。", "确定");
                return;
            }
            generating = true;
            preview = EditorSceneManager.NewPreviewScene();
            var screen = new GameObject("Eaststar_Display");
            SceneManager.MoveGameObjectToScene(screen, preview);
            CreateScreen(screen);
            var console = new GameObject("Eaststar_Console");
            SceneManager.MoveGameObjectToScene(console, preview);
            CreateConsole(console);
            var secondary = new GameObject("Eaststar_SecondaryDisplay");
            SceneManager.MoveGameObjectToScene(secondary, preview);
            CreateSecondaryScreen(secondary);
            string screenPath = Prefabs + "/Eaststar_Display.prefab";
            string consolePath = Prefabs + "/Eaststar_Console.prefab";
            SavePrefab(screen, screenPath);
            SavePrefab(console, consolePath);
            SavePrefab(secondary, Prefabs + "/Eaststar_SecondaryDisplay.prefab");
            AssetDatabase.SaveAssets();
            // Re-read the persisted objects instead of retaining temporary Udon references.
            ValidatePrefabs(screenPath, consolePath);
            var savedSecondary = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Eaststar_SecondaryDisplay.prefab");
            if (!savedSecondary) throw new IOException("无法读取副屏预制件。");
            var secondaryScript = savedSecondary.GetComponent<EaststarSecondaryScreen>();
            RequireBacking(secondaryScript, savedSecondary);
            if (!secondaryScript.standbySurface || !secondaryScript.videoSurface ||
                savedSecondary.GetComponentInChildren<AudioSource>(true) || savedSecondary.GetComponentInChildren<VRCUnityVideoPlayer>(true))
                throw new InvalidOperationException("副屏引用不完整或含有播放器/音源。");
            Debug.Log("[极星播放器 v0.3.13] 三个预制件已保存并通过引用检查。");
        }
        catch (Exception ex)
        {
            Debug.LogError("[极星播放器] 生成失败，请查看以下完整调用栈。");
            Debug.LogException(ex);
        }
        finally
        {
            if (preview.IsValid())
            {
                // Let editor callbacks finish before disposing temporary objects as a whole.
                Scene closing = preview;
                EditorApplication.delayCall += () =>
                {
                    try { if (closing.IsValid()) EditorSceneManager.ClosePreviewScene(closing); }
                    catch (Exception ex) { Debug.LogException(ex); }
                    finally { generating = false; }
                };
            }
            else generating = false;
        }
    }
    private static void SavePrefab(GameObject root, string path)
    {
        bool success;
        var asset = PrefabUtility.SaveAsPrefabAsset(root, path, out success);
        if (!success || !asset) throw new IOException("保存预制件失败：" + path);
    }
    private static UdonBehaviour RequireBacking(UdonSharpBehaviour proxy, GameObject root)
    {
        if (!proxy) throw new InvalidOperationException("找不到 UdonSharp 组件：" + root.name);
        var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
        if (!backing || backing.gameObject != root || !backing.programSource)
            throw new InvalidOperationException("UdonBehaviour 引用无效：" + root.name + "。请确认程序资源已编译。");
        return backing;
    }
    private static void CheckEvent(UnityEventBase evt, UdonBehaviour backing, string label)
    {
        if (evt.GetPersistentEventCount() != 1 || evt.GetPersistentTarget(0) != backing ||
            evt.GetPersistentMethodName(0) != "SendCustomEvent")
            throw new InvalidOperationException("预制件事件引用无效：" + label);
    }
    private static void ValidatePrefabs(string screenPath, string consolePath)
    {
        var screen = AssetDatabase.LoadAssetAtPath<GameObject>(screenPath);
        var console = AssetDatabase.LoadAssetAtPath<GameObject>(consolePath);
        if (!screen || !console) throw new IOException("无法读取已保存的预制件。");
        var s = screen.GetComponent<EaststarScreen>();
        RequireBacking(s, screen);
        if (!s.videoPlayer || !s.audioSource || !s.videoSurface || !s.standbySurface || !s.mainPlaybackIcon || !s.mainPlaybackImage || !s.playIconTexture || !s.pauseIconTexture)
            throw new InvalidOperationException("显示屏组件引用不完整。");
        var c = console.GetComponent<EaststarConsole>();
        var backing = RequireBacking(c, console);
        if (!c.urlInput || !c.statusText || !c.prefixText || !c.progressText || !c.controllerText || !c.volumeSlider || !c.progressSlider)
            throw new InvalidOperationException("控制台组件引用不完整。");
        if (!s.durationProbe || !s.durationProbe.player || !c.skipButton || !c.stopButton || !c.prevButton || !c.nextButton || !c.queueHeader || !c.nowPlayingText || !c.queuePageText || c.queueRows == null || c.queueRows.Length != 5) throw new InvalidOperationException("播放列表引用不完整。");
        var buttons = console.GetComponentsInChildren<Button>(true);
        if (buttons.Length != 8 || !c.localPauseButton || !c.localPauseLabel) throw new InvalidOperationException("控制台按钮数量或本地暂停引用不正确（应为八个）。");
        foreach (var button in buttons) CheckEvent(button.onClick, backing, button.name);
        ValidateInteraction(c);
        CheckEvent(c.volumeSlider.onValueChanged, backing, "音量");
        CheckEvent(c.progressSlider.onValueChanged, backing, "进度");
    }
    private static Font FindChineseFont()
    {
        var selected = Selection.activeObject as Font;
        if (SupportsChinese(selected)) return selected;
        foreach (string guid in AssetDatabase.FindAssets("t:Font", new[] { "Assets" }))
        {
            var candidate = AssetDatabase.LoadAssetAtPath<Font>(AssetDatabase.GUIDToAssetPath(guid));
            if (SupportsChinese(candidate)) return candidate;
        }
        return null;
    }
    private static bool SupportsChinese(Font candidate)
    {
        if (!candidate || !candidate.dynamic) return false;
        // Check all Chinese characters used by the fixed UI and runtime messages.
        string chars = "极星播放器视频控制台待机中未连接显示屏模式直接解析前缀请输入网址停止重置重新同步本地音量进度调整当前总时长暂无你是可以仅者已离开锁定等待加载正在超时失败检查结束提交请求保留并在末尾粘贴有效五秒后试或框不支持和房间作者粘贴求有点击测试交互正常本地暂停恢复播放抓握区域个人返回播放屏幕设置添加列表跳过上一页下一页清空未知读取房主";
        foreach (char c in chars) if (!candidate.HasCharacter(c)) return false;
        return true;
    }
    private static bool EnsureProgram(string name)
    {
        string path = Root + "/Runtime/" + name + ".asset";
        string sourcePath = Root + "/Runtime/" + name + ".cs";
        var source = AssetDatabase.LoadAssetAtPath<MonoScript>(sourcePath);
        if (!source) throw new FileNotFoundException("找不到 UdonSharp 源码", sourcePath);
        UdonSharpProgramAsset existing = null;
        foreach (string guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset", new[] { "Assets" }))
        {
            var candidate = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(AssetDatabase.GUIDToAssetPath(guid));
            if (!candidate || candidate.sourceCsScript != source) continue;
            if (existing) throw new InvalidOperationException("同一源码存在多个 UdonSharp 程序资源：" + sourcePath);
            existing = candidate;
        }
        if (existing) return false;
        if (!AssetDatabase.IsValidFolder(Root + "/Runtime")) throw new DirectoryNotFoundException(Root + "/Runtime");
        if (AssetDatabase.LoadMainAssetAtPath(path)) throw new IOException("目标路径已存在非 UdonSharp 资源：" + path);
        var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
        asset.sourceCsScript = source;
        AssetDatabase.CreateAsset(asset, path);
        return true;
    }
    [MenuItem("Tools/Eaststar Player/2 - Add and Link Both Prefabs")]
    public static void AddBoth()
    {
        if (generating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || !ResolveRoot()) return;
        string screenPath = Prefabs + "/Eaststar_Display.prefab";
        string consolePath = Prefabs + "/Eaststar_Console.prefab";
        try
        {
            ValidatePrefabs(screenPath, consolePath);
            var s = AssetDatabase.LoadAssetAtPath<GameObject>(screenPath);
            var c = AssetDatabase.LoadAssetAtPath<GameObject>(consolePath);
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Add Eaststar Player");
            var screen = (GameObject)PrefabUtility.InstantiatePrefab(s);
            Undo.RegisterCreatedObjectUndo(screen, "Add Eaststar Display");
            var console = (GameObject)PrefabUtility.InstantiatePrefab(c);
            Undo.RegisterCreatedObjectUndo(console, "Add Eaststar Console");
            screen.transform.position = new Vector3(0,2,4); console.transform.position = new Vector3(0,1.3f,1.7f);
            var control = console.GetComponent<EaststarConsole>();
            var display = screen.GetComponent<EaststarScreen>();
            RequireBacking(control, console);
            RequireBacking(display, screen);
            var serialized = new SerializedObject(control);
            var screenField = serialized.FindProperty("screen");
            if (screenField == null) throw new InvalidOperationException("控制台缺少 screen 字段。");
            screenField.objectReferenceValue = display;
            serialized.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(control);
            EditorUtility.SetDirty(control);
            EditorSceneManager.MarkSceneDirty(console.scene);
            EnsureSceneEventSystem(console.scene);
            if (!LinkScreens(false)) Debug.LogWarning("[极星播放器] 自动配对未完成，请检查 Console 并执行菜单3。");
            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = console;
            Debug.Log("[极星播放器] 显示屏与控制台已连接。");
        }
        catch (Exception ex)
        {
            Debug.LogError("[极星播放器] 添加失败。请先生成新版预制件，并检查完整调用栈；如已添加部分对象，可撤销。");
            Debug.LogException(ex);
        }
    }
    private static Material MakeMaterial(string name, Texture texture)
    {
        string path = Generated + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!mat) { mat = new Material(Shader.Find("Unlit/Texture")); AssetDatabase.CreateAsset(mat,path); }
        mat.mainTexture = texture; mat.mainTextureScale = Vector2.one; mat.mainTextureOffset = Vector2.zero; EditorUtility.SetDirty(mat); return mat;
    }
    private static GameObject Surface(Transform parent, string name, Material mat, float w, float h, float z)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad); go.name = name;
        go.transform.SetParent(parent,false); go.transform.localPosition = new Vector3(0,0,z); go.transform.localScale = new Vector3(w,h,1);
        UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
        go.GetComponent<MeshRenderer>().sharedMaterial = mat; return go;
    }
    private static void CreateScreen(GameObject go)
    {
        var image = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/Standby.png");
        if (!image) throw new Exception("Standby.png is missing.");
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(Generated + "/Video.renderTexture");
        if (!rt) { rt = new RenderTexture(1920,1080,0,RenderTextureFormat.ARGB32); rt.name="Eaststar Video"; AssetDatabase.CreateAsset(rt,Generated + "/Video.renderTexture"); }
        // Cover the entire 16:9 display using a centered UV crop; no stretching or image changes.
        var standbyMaterial = MakeMaterial("Standby", image);
        float imageAspect = (float)image.width / image.height;
        float screenAspect = 16f / 9f;
        var uvScale = new Vector2(Mathf.Min(1f, screenAspect / imageAspect), Mathf.Min(1f, imageAspect / screenAspect));
        standbyMaterial.mainTextureScale = uvScale;
        standbyMaterial.mainTextureOffset = (Vector2.one - uvScale) * .5f;
        EditorUtility.SetDirty(standbyMaterial);
        var standby = Surface(go.transform,"Standby Logo",standbyMaterial,3.6f,2.025f,-.002f);
        var video = Surface(go.transform,"Video Surface",MakeMaterial("Video",rt),3.6f,2.025f,0);
        var back = GameObject.CreatePrimitive(PrimitiveType.Quad); back.name="Black Screen Backdrop"; back.transform.SetParent(go.transform,false); back.transform.localPosition=new Vector3(0,0,.004f); back.transform.localScale=new Vector3(3.6f,2.025f,1); UnityEngine.Object.DestroyImmediate(back.GetComponent<Collider>());
        string mp=Generated+"/Black.mat"; var black=AssetDatabase.LoadAssetAtPath<Material>(mp); if(!black){black=new Material(Shader.Find("Unlit/Color")); black.color=Color.black; AssetDatabase.CreateAsset(black,mp);} back.GetComponent<MeshRenderer>().sharedMaterial=black;
        video.SetActive(false);
        var audio = go.AddComponent<AudioSource>(); audio.playOnAwake=false; audio.spatialBlend=0; audio.volume=.6f;
        var player = go.AddComponent<VRCUnityVideoPlayer>();
        var so = new SerializedObject(player);
        Need(so,"autoPlay").boolValue=false; Need(so,"loop").boolValue=false; Need(so,"renderMode").enumValueIndex=0; Need(so,"targetTexture").objectReferenceValue=rt;
        var sources=Need(so,"targetAudioSources"); sources.arraySize=1; sources.GetArrayElementAtIndex(0).objectReferenceValue=audio;
        var resolution=so.FindProperty("maximumResolution"); if(resolution!=null) resolution.intValue=1080;
        so.ApplyModifiedPropertiesWithoutUndo();
        var script=go.AddUdonSharpComponent<EaststarScreen>(); script.videoPlayer=player; script.audioSource=audio; script.standbySurface=standby; script.videoSurface=video;
        AddDurationProbe(script);
        RequireBacking(script, go);
        EnsureMainPlaybackControls(script);
        EditorUtility.SetDirty(script);
    }
    private static void AddDurationProbe(EaststarScreen script)
    {
        if (script.durationProbe) return;
        var child = new GameObject("Queue Duration Probe");
        child.transform.SetParent(script.transform, false);
        if (!EditorSceneManager.IsPreviewScene(script.gameObject.scene)) Undo.RegisterCreatedObjectUndo(child,"Add queue duration probe");
        var player = child.AddComponent<VRCUnityVideoPlayer>();
        var rtPath = Generated + "/DurationProbe.renderTexture";
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
        if (!rt) { rt = new RenderTexture(64,64,0,RenderTextureFormat.ARGB32); AssetDatabase.CreateAsset(rt,rtPath); }
        var so = new SerializedObject(player);
        Need(so,"autoPlay").boolValue=false; Need(so,"loop").boolValue=false; Need(so,"renderMode").enumValueIndex=0; Need(so,"targetTexture").objectReferenceValue=rt;
        Need(so,"targetAudioSources").arraySize=0;
        var resolution=so.FindProperty("maximumResolution"); if(resolution!=null) resolution.intValue=360;
        so.ApplyModifiedPropertiesWithoutUndo();
        var probe = child.AddUdonSharpComponent<EaststarDurationProbe>(); probe.player=player; probe.screen=script; script.durationProbe=probe;
        RequireBacking(probe,child); EditorUtility.SetDirty(probe); EditorUtility.SetDirty(script);
    }
    private static void CreateSecondaryScreen(GameObject go)
    {
        var standbyMat = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Standby.mat");
        var videoMat = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Video.mat");
        var black = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Black.mat");
        if (!standbyMat || !videoMat || !black) throw new InvalidOperationException("请先生成主屏材质。");
        var standby = Surface(go.transform,"Standby Logo",standbyMat,3.6f,2.025f,-.002f);
        var video = Surface(go.transform,"Video Surface",videoMat,3.6f,2.025f,0);
        Surface(go.transform,"Black Screen Backdrop",black,3.6f,2.025f,.004f);
        video.SetActive(false);
        var script = go.AddUdonSharpComponent<EaststarSecondaryScreen>();
        script.standbySurface = standby;
        script.videoSurface = video;
        RequireBacking(script, go);
        EditorUtility.SetDirty(script);
    }

    [MenuItem("Tools/Eaststar Player/3 - Link Screens by ID")]
    public static void LinkScreensMenu() { LinkScreens(true); }

    [MenuItem("Tools/Eaststar Player/4 - Add Secondary Display")]
    public static void AddSecondary()
    {
        if (generating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || !ResolveRoot()) return;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/Eaststar_SecondaryDisplay.prefab");
        if (!prefab) { Debug.LogError("[极星播放器] 请先运行菜单 1 生成新版预制件。"); return; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Add Eaststar Secondary Display");
        go.transform.position = new Vector3(4, 2, 4);
        Selection.activeGameObject = go;
        LinkScreens(true);
    }

    // Scene-only linking: no GameObject.Find, no runtime discovery, no extra video decoder.
    // Run manually for ClientSim, automatically before SDK world build.
    internal static bool LinkScreens(bool report)
    {
        if (generating || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode) return false;
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded) return false;
        var mains = new List<EaststarScreen>();
        var subs = new List<EaststarSecondaryScreen>();
        foreach (var go in scene.GetRootGameObjects())
        {
            mains.AddRange(go.GetComponentsInChildren<EaststarScreen>(true));
            subs.AddRange(go.GetComponentsInChildren<EaststarSecondaryScreen>(true));
        }
        if (mains.Count == 0 && subs.Count == 0) return true;
        if (!ResolveRoot()) return false;
        try
        {
            EnsureChildFolder("Generated");
            var byId = new Dictionary<int, EaststarScreen>();
            foreach (var main in mains)
            {
                if (main.secondaryScreenId <= 0) throw new InvalidOperationException("主屏编号必须为正整数：" + main.name);
                if (byId.ContainsKey(main.secondaryScreenId)) throw new InvalidOperationException("主屏编号重复：" + main.secondaryScreenId + "。请为不同主屏设置唯一编号。");
                if (!main.videoPlayer || !main.videoSurface || !main.videoSurface.GetComponent<Renderer>())
                    throw new InvalidOperationException("主屏缺少视频引用：" + main.name);
                RequireBacking(main, main.gameObject);
                if (!main.durationProbe) { if (!report) throw new InvalidOperationException("请先执行菜单 3，为旧主屏安装时长读取组件。"); Undo.RecordObject(main,"Add queue probe"); AddDurationProbe(main); PrefabUtility.RecordPrefabInstancePropertyModifications(main); EditorSceneManager.MarkSceneDirty(main.gameObject.scene); }
                EnsureMainPlaybackControls(main);
                byId.Add(main.secondaryScreenId, main);
            }
            foreach (var sub in subs)
            {
                RequireBacking(sub, sub.gameObject);
                if (!sub.videoSurface || !sub.standbySurface || !sub.videoSurface.GetComponent<Renderer>())
                    throw new InvalidOperationException("副屏缺少画面引用：" + sub.name);
            }
            // Each ID gets its own video target, so separate main players cannot overwrite one another.
            var materials = new Dictionary<int, Material>();
            foreach (var entry in byId)
            {
                int id = entry.Key; var main = entry.Value;
                string key = "Video_Group_" + id;
                string rtPath = Generated + "/" + key + ".renderTexture";
                var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
                if (!rt)
                {
                    rt = new RenderTexture(1920, 1080, 0, RenderTextureFormat.ARGB32);
                    rt.name = key;
                    AssetDatabase.CreateAsset(rt, rtPath);
                }
                var mat = MakeMaterial(key, rt);
                materials.Add(id, mat);
                var serialized = new SerializedObject(main.videoPlayer);
                var target = Need(serialized, "targetTexture");
                if (target.objectReferenceValue != rt)
                {
                    target.objectReferenceValue = rt;
                    serialized.ApplyModifiedProperties();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(main.videoPlayer);
                    EditorSceneManager.MarkSceneDirty(main.gameObject.scene);
                }
                SetRendererMaterial(main.videoSurface.GetComponent<Renderer>(), mat);
            }
            int linked = 0;
            foreach (var sub in subs)
            {
                EaststarScreen main;
                byId.TryGetValue(sub.mainScreenId, out main);
                var serialized = new SerializedObject(sub);
                var field = Need(serialized, "mainScreen");
                if (field.objectReferenceValue != main)
                {
                    field.objectReferenceValue = main;
                    serialized.ApplyModifiedProperties();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(sub);
                    EditorUtility.SetDirty(sub);
                    EditorSceneManager.MarkSceneDirty(sub.gameObject.scene);
                }
                var fallback = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Video.mat");
                if (main)
                {
                    SetRendererMaterial(sub.videoSurface.GetComponent<Renderer>(), materials[sub.mainScreenId]);
                    linked++;
                }
                else
                {
                    if (fallback) SetRendererMaterial(sub.videoSurface.GetComponent<Renderer>(), fallback);
                    Debug.LogWarning("[极星播放器] 副屏 " + sub.name + "（主屏编号 " + sub.mainScreenId + "）未找到主屏，将保持待机。", sub);
                }
            }
            // Reuse the actual configured URL prefix for each main screen, not a runtime-constructed URL.
            foreach (var main in mains)
            {
                Undo.RecordObject(main, "Personal console URL preset");
                main.personalDefaultAddress = EditionDefaultUrl();
                bool found = false;
                foreach (var sceneRoot in scene.GetRootGameObjects())
                {
                    foreach (var control in sceneRoot.GetComponentsInChildren<EaststarConsole>(true))
                    {
                        if (control.screen != main) continue;
                        main.personalDefaultAddress = control.defaultInputAddress;
                        if (!font && control.statusText && control.statusText.font) font = control.statusText.font;
                        found = true; break;
                    }
                    if (found) break;
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(main);
                EditorUtility.SetDirty(main);
            }
            if (!font) font = FindChineseFont();
            LinkPersonalViews(scene, mains);
            foreach (var sceneRoot in scene.GetRootGameObjects())
                foreach (var control in sceneRoot.GetComponentsInChildren<EaststarConsole>(true))
                    EnsureConsolePause(control);
            EnsureSceneEventSystem(scene);
            AssetDatabase.SaveAssets();
            if (report) Debug.Log("[极星播放器] 编号配对完成：主屏 " + mains.Count + "，已连接副屏 " + linked + "/" + subs.Count + "。请保存场景。");
            return true;
        }
        catch (Exception ex) { Debug.LogException(ex); return false; }
    }

    // No prefab dragging, custom layer or inspector reference is needed for personal screens.
    // Both platforms share a world-space pickup template. Preserve the old VR root name for upgrades.
    private const string PersonalDesktopName = "__Eaststar_Local_Desktop";
    private const string PersonalVRName = "__Eaststar_Local_VR";
    private static GameObject FindPersonalRoot(Scene scene, string name)
    {
        GameObject found = null;
        foreach (var go in scene.GetRootGameObjects())
        {
            if (go.name != name) continue;
            if (found) throw new InvalidOperationException("个人屏幕根物体重复：" + name);
            found = go;
        }
        return found;
    }
    private static GameObject PersonalRoot(Scene scene, string name)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, scene);
        Undo.RegisterCreatedObjectUndo(go, "Create Eaststar personal view");
        return go;
    }
    private static void LinkPersonalViews(Scene scene, List<EaststarScreen> mains)
    {
        if (mains.Count == 0) return;
        // Stable single listener; at runtime it chooses the closest active main screen.
        mains.Sort((a,b) => a.secondaryScreenId.CompareTo(b.secondaryScreenId));
        var host = mains[0];
        // Migrate v0.3.3: remove only the generated legacy desktop overlay.
        var desktop = FindPersonalRoot(scene, PersonalDesktopName);
        if (desktop)
        {
            var legacyCanvas = desktop.GetComponent<Canvas>();
            if (legacyCanvas && legacyCanvas.renderMode == RenderMode.ScreenSpaceOverlay &&
                desktop.GetComponentInChildren<RawImage>(true))
                Undo.DestroyObjectImmediate(desktop);
            else
                throw new InvalidOperationException("旧个人全屏物体名称冲突：" + PersonalDesktopName);
        }
        var vr = FindPersonalRoot(scene, PersonalVRName);
        if (!vr)
        {
            vr = PersonalRoot(scene, PersonalVRName);
            vr.layer = 13; // SDK Pickup layer
            var body = vr.AddComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
            var box = vr.AddComponent<BoxCollider>(); box.size = new Vector3(.82f,.48f,.035f);
            var pickup = vr.AddComponent<VRCPickup>();
            pickup.pickupable = true; pickup.proximity = 1.5f;
            pickup.DisallowTheft = false;
            // Pickup hold settings are applied below for both newly created and existing templates.
            var standbyMat = host.standbySurface.GetComponent<Renderer>().sharedMaterial;
            var front = Surface(vr.transform,"Personal Video",standbyMat,.8f,.45f,-.018f);
            front.layer = 13;
            var black = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Black.mat");
            if (!black) black = MakeMaterial("PersonalBlack", Texture2D.blackTexture);
            var back = Surface(vr.transform,"Personal Frame",black,.82f,.48f,-.015f);
            back.layer = 13;
            var rear = Surface(vr.transform,"Personal Back",black,.82f,.48f,.018f);
            rear.transform.localRotation = Quaternion.Euler(0,180,0); rear.layer = 13;
        }
        var vrImageObject = vr.transform.Find("Personal Video");
        var vrImage = vrImageObject ? vrImageObject.GetComponent<Renderer>() : null;
        var vrPickup = vr.GetComponent<VRCPickup>();
        var vrBody = vr.GetComponent<Rigidbody>();
        if (!vrImage || !vrPickup || !vrBody || !vr.GetComponent<BoxCollider>() ||
            vr.GetComponent<VRCObjectSync>() || vr.GetComponentInChildren<AudioSource>(true) ||
            vr.GetComponentInChildren<VRCUnityVideoPlayer>(true))
            throw new InvalidOperationException("个人 VR 屏幕结构损坏，请删除 " + PersonalVRName + " 后重新执行菜单3。");
        Undo.RecordObject(vrPickup, "Configure shared personal pickup");
        vrPickup.AutoHold = VRC.SDKBase.VRC_Pickup.AutoHoldMode.Yes;
        vrPickup.allowManipulationWhenEquipped = true;
        vrPickup.pickupable = true;
        vrPickup.proximity = 1.5f;
        EditorUtility.SetDirty(vrPickup);
        Undo.RecordObject(vrBody, "Configure floating screen physics");
        vrBody.useGravity = false;
        vrBody.isKinematic = true;
        EditorUtility.SetDirty(vrBody);
        // The video plane is clickable; only the separate bottom grip is the pickup hit area.
        var grip = vr.GetComponent<BoxCollider>();
        Undo.RecordObject(grip, "Separate personal screen click and pickup grip");
        grip.center = new Vector3(0,-.263f,0);
        grip.size = new Vector3(.42f,.064f,.045f);
        EditorUtility.SetDirty(grip);
        var gripArt = vr.transform.Find("Personal Pickup Grip");
        if (!gripArt)
        {
            var gripMat = AssetDatabase.LoadAssetAtPath<Material>(Generated + "/Black.mat");
            var handle = Surface(vr.transform,"Personal Pickup Grip",gripMat,.42f,.064f,-.023f);
            handle.layer = 13;
            handle.transform.localPosition = new Vector3(0,-.263f,-.023f);
            Undo.RegisterCreatedObjectUndo(handle,"Add pickup grip");
        }
        var gripLabel = vr.transform.Find("Personal Pickup Grip Label") as Transform;
        TextMesh gripText = null;
        if (!gripLabel)
        {
            var go = new GameObject("Personal Pickup Grip Label", typeof(TextMesh));
            go.transform.SetParent(vr.transform, false);
            gripText = go.GetComponent<TextMesh>();
            Undo.RegisterCreatedObjectUndo(go, "Add pickup grip label");
        }
        else
        {
            gripText = gripLabel.GetComponent<TextMesh>();
            if (!gripText) gripText = gripLabel.gameObject.AddComponent<TextMesh>();
            Undo.RecordObject(gripLabel, "Configure pickup grip label");
        }
        if (gripText)
        {
            // Reposition both new and existing labels onto the front face of the black grip.
            var labelTransform = gripText.transform;
            Undo.RecordObject(labelTransform, "Center pickup grip label");
            labelTransform.localPosition = new Vector3(0f, -.263f, -.027f);
            labelTransform.localRotation = Quaternion.identity;
            labelTransform.localScale = Vector3.one;
            gripText.gameObject.layer = 13;
            EditorUtility.SetDirty(labelTransform);
            EditorUtility.SetDirty(gripText.gameObject);
            Undo.RecordObject(gripText, "Configure pickup grip label text");
            gripText.text = "抓握区域";
            gripText.anchor = TextAnchor.MiddleCenter;
            gripText.alignment = TextAlignment.Center;
            gripText.characterSize = 0.006f;
            gripText.fontSize = 48;
            gripText.color = white;
            gripText.richText = false;
            if (font) gripText.font = font;
            var renderer = gripText.GetComponent<MeshRenderer>();
            if (renderer && gripText.font) renderer.sharedMaterial = gripText.font.material;
            EditorUtility.SetDirty(gripText);
        }
        CanvasGroup ignoredGroup; RawImage ignoredImage;
        EnsurePlaybackCanvas(vrImageObject, host, true, out ignoredGroup, out ignoredImage);
        vr.SetActive(false);
        foreach (var main in mains)
        {
            Undo.RecordObject(main,"Link local personal view");
            bool enabled = main == host;
            main.personalSources = enabled ? mains.ToArray() : new EaststarScreen[0];
            main.personalVR = enabled ? vr : null;
            main.personalVRImage = enabled ? vrImage : null;
            main.personalPickup = enabled ? vrPickup : null;
            main.personalBody = enabled ? vrBody : null;
            PrefabUtility.RecordPrefabInstancePropertyModifications(main);
            EditorUtility.SetDirty(main);
        }
        EditorSceneManager.MarkSceneDirty(scene);
    }

    // Add the new button to the existing working console without rebuilding its Canvas.
    private static void EnsureConsolePause(EaststarConsole script)
    {
        if (!script || !script.urlInput) return;
        var canvas = script.urlInput.GetComponentInParent<Canvas>(true);
        if (!canvas) return;
        var rect = (RectTransform)canvas.transform;
        var sync = rect.Find("重新同步");
        var add = rect.Find("添加到列表");
        var stop = rect.Find("停止并清空");
        var reset = rect.Find("重置网址");
        if (!sync || !add || !stop || !reset)
            throw new InvalidOperationException("本地暂停需要 v0.3.0 或以上控制台，请先生成并替换旧预制件。");
        bool sceneObject = !EditorSceneManager.IsPreviewScene(script.gameObject.scene);
        var pause = rect.Find("本地暂停");
        var udon = RequireBacking(script,script.gameObject);
        if (!pause)
        {
            // Clone only a known-working button, not the whole Canvas or sample logic.
            if (sceneObject) Undo.RegisterFullObjectHierarchyUndo(script.gameObject,"Add local pause control");
            var created = UnityEngine.Object.Instantiate(sync.gameObject,rect,false);
            created.name = "本地暂停";
            if (sceneObject) Undo.RegisterCreatedObjectUndo(created,"Add local pause button");
            pause = created.transform;
        }
        var button = pause.GetComponent<Button>();
        var label = pause.GetComponentInChildren<Text>(true);
        if (!button || !label) throw new InvalidOperationException("本地暂停按钮结构缺失。");
        // Left console column has 704px width. Fit five equal buttons inside the old row.
        Transform[] row = {add,stop,reset,sync,pause};
        float rowCenter = -380f;
        for (int i=0;i<row.Length;i++)
        {
            var r = (RectTransform)row[i];
            r.anchoredPosition = new Vector2(rowCenter+(i-2)*142f,-25f);
            r.sizeDelta = new Vector2(134f,52f);
            var t = row[i].GetComponentInChildren<Text>(true);
            if (t) { t.rectTransform.sizeDelta = new Vector2(126f,50f); t.fontSize=18; }
            if (sceneObject)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                if(t) { PrefabUtility.RecordPrefabInstancePropertyModifications(t); PrefabUtility.RecordPrefabInstancePropertyModifications(t.rectTransform); }
            }
        }
        label.text = "本地暂停";
        label.raycastTarget = false;
        NoNavigation(button);
        if (button.targetGraphic) button.targetGraphic.raycastTarget = true;
        BindButton(button,udon,"ToggleLocalPause");
        button.interactable = false;
        script.localPauseButton=button; script.localPauseLabel=label;
        CompactMainConsole(script,rect,sceneObject);
        EditorUtility.SetDirty(script);
        if (sceneObject)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(script);
            PrefabUtility.RecordPrefabInstancePropertyModifications(button);
            PrefabUtility.RecordPrefabInstancePropertyModifications(label);
            EditorSceneManager.MarkSceneDirty(script.gameObject.scene);
        }
    }

    // Compact visual styling only: preserve the Canvas, SDK hit areas, events and serialized references.
    // Absolute sizes make this safe to run repeatedly on existing scene instances.
    private static void CompactRect(RectTransform r, float width, float height, bool record)
    {
        if (!r) return;
        if (record) Undo.RecordObject(r, "Compact Eaststar UI");
        r.sizeDelta = new Vector2(width, height);
        if (record) { PrefabUtility.RecordPrefabInstancePropertyModifications(r); EditorUtility.SetDirty(r); }
    }
    private static void CompactText(Text t, int size, bool record)
    {
        if (!t) return;
        if (record) Undo.RecordObject(t, "Compact Eaststar typography");
        t.fontSize = size;
        t.resizeTextForBestFit = false;
        if (record) { PrefabUtility.RecordPrefabInstancePropertyModifications(t); EditorUtility.SetDirty(t); }
    }
    private static void CompactLabel(Transform root, string name, int size, bool record)
    {
        var child = root.Find(name);
        if (child) CompactText(child.GetComponent<Text>(), size, record);
    }
    private static void CompactButton(Transform root, string name, int size, float height, bool record)
    {
        var child = root.Find(name);
        if (!child) return;
        var r = child as RectTransform;
        if (!r) return;
        CompactRect(r, r.sizeDelta.x, height, record);
        var t = child.GetComponentInChildren<Text>(true);
        if (t) { CompactText(t, size, record); CompactRect(t.rectTransform, r.sizeDelta.x - 8f, height - 2f, record); }
    }
    private static void CompactSlider(Slider slider, float trackHeight, float knobWidth, float knobHeight, bool record)
    {
        if (!slider) return;
        var track = slider.transform.Find("Track") as RectTransform;
        var fillArea = slider.transform.Find("Fill Area") as RectTransform;
        if (track) CompactRect(track, track.sizeDelta.x, trackHeight, record);
        if (fillArea) CompactRect(fillArea, fillArea.sizeDelta.x, trackHeight, record);
        // Slider drives Handle anchors across the full cross-axis of Handle Area.
        // Keep that rect as a transparent interaction target and draw a separate fixed-size dot.
        if (slider.handleRect)
        {
            var handle = slider.handleRect;
            var handleImage = handle.GetComponent<Image>();
            if (!handleImage) handleImage = EnsureUIComponent<Image>(handle.gameObject, record);
            if (record) { Undo.RecordObject(handleImage, "Hide stretched slider handle"); Undo.RecordObject(slider, "Set fixed slider thumb"); }
            handleImage.color = Color.clear;
            handleImage.raycastTarget = true;
            CompactRect(handle, Mathf.Max(18f, knobWidth), 0f, record);
            handle.anchoredPosition = Vector2.zero;
            var dotTransform = handle.Find("Compact Thumb");
            Image dot;
            if (!dotTransform)
            {
                dot = Block(handle,"Compact Thumb",0,0,knobWidth,knobHeight,white);
                if (record) Undo.RegisterCreatedObjectUndo(dot.gameObject,"Create fixed-size slider thumb");
            }
            else dot = EnsureUIComponent<Image>(dotTransform.gameObject, record);
            var r = dot.rectTransform;
            if (record) { Undo.RecordObject(r,"Center slider thumb"); Undo.RecordObject(dot,"Style slider thumb"); }
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f,.5f);
            r.anchoredPosition = Vector2.zero;
            r.localScale = Vector3.one;
            r.localRotation = Quaternion.identity;
            r.sizeDelta = new Vector2(knobWidth,knobHeight);
            dot.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
            dot.type = Image.Type.Simple;
            dot.color = white;
            dot.raycastTarget = true;
            slider.targetGraphic = dot;
            if (record)
            {
                foreach (var c in new Component[] {handle,handleImage,r,dot,slider})
                { PrefabUtility.RecordPrefabInstancePropertyModifications(c); EditorUtility.SetDirty(c); }
            }
        }
        // Preserve the root's transparent 40-46px hit area, event bindings and permission checks.
    }
    private static void CompactMainConsole(EaststarConsole script, RectTransform root, bool record)
    {
        CompactLabel(root,"Title",23,record);
        CompactLabel(root,"Subtitle",13,record);
        CompactLabel(root,"Status",15,record);
        CompactLabel(root,"Prefix",12,record);
        CompactLabel(root,"Volume title",14,record);
        CompactLabel(root,"Progress title",16,record);
        CompactLabel(root,"Progress time",15,record);
        CompactLabel(root,"Controller",13,record);
        CompactLabel(root,"Queue title",21,record);
        CompactLabel(root,"Now Playing",14,record);
        CompactLabel(root,"Queue page",14,record);
        CompactLabel(root,"Queue permission",13,record);
        CompactLabel(root,"Author",12,record);
        var url = root.Find("URL Input") as RectTransform;
        if (url)
        {
            CompactRect(url,url.sizeDelta.x,42f,record);
            if (script.urlInput.textComponent)
            {
                var t = script.urlInput.textComponent;
                CompactText(t,16,record); CompactRect(t.rectTransform,t.rectTransform.sizeDelta.x,38f,record);
            }
            var placeholder = script.urlInput.placeholder as Text;
            if (placeholder) { CompactText(placeholder,15,record); CompactRect(placeholder.rectTransform,placeholder.rectTransform.sizeDelta.x,38f,record); }
        }
        string[] buttons = {"添加到列表","停止并清空","重置网址","重新同步","本地暂停","上一页","下一页","跳过视频"};
        foreach (string name in buttons) CompactButton(root,name,15,40f,record);
        for (int i=0;i<5;i++)
        {
            var row=root.Find("Queue Row "+i) as RectTransform;
            if (!row) continue;
            CompactRect(row,row.sizeDelta.x,48f,record);
            var label=row.GetComponentInChildren<Text>(true);
            if (label) { CompactText(label,14,record); CompactRect(label.rectTransform,label.rectTransform.sizeDelta.x,46f,record); }
        }
        CompactSlider(script.volumeSlider,4f,10f,10f,record);
        CompactSlider(script.progressSlider,5f,10f,10f,record);
    }

    private static void EnsureMainPlaybackControls(EaststarScreen main)
    {
        if (!main || !main.videoSurface) return;
        CanvasGroup group; RawImage art;
        EnsurePlaybackCanvas(main.videoSurface.transform, main, false, out group, out art);
        main.mainPlaybackIcon=group; main.mainPlaybackImage=art;
        main.playIconTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/PlayIcon.png");
        main.pauseIconTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/PauseIcon.png");
        if(!main.playIconTexture || !main.pauseIconTexture) throw new FileNotFoundException("缺少播放/暂停图标资源。");
        EditorUtility.SetDirty(main);
        if (!EditorSceneManager.IsPreviewScene(main.gameObject.scene))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(main);
            EditorSceneManager.MarkSceneDirty(main.gameObject.scene);
        }
    }

    private static void EnsurePlaybackCanvas(Transform surface, EaststarScreen target, bool personal,
        out CanvasGroup group, out RawImage artwork)
    {
        var parent=surface.parent;
        var existing=parent.Find("Local Playback UI");
        bool sceneObject=!EditorSceneManager.IsPreviewScene(parent.gameObject.scene);
        GameObject go;
        if (!existing)
        {
            go=new GameObject("Local Playback UI",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster));
            go.transform.SetParent(parent,false);
            if(sceneObject) Undo.RegisterCreatedObjectUndo(go,"Add local screen controls");
        }
        else go=existing.gameObject;
        var rect=(RectTransform)go.transform;
        rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);
        rect.sizeDelta=new Vector2(1000f,562.5f);
        rect.localRotation=surface.localRotation;
        rect.localPosition=surface.localPosition+surface.localRotation*Vector3.back*(personal ? .007f : .015f);
        rect.localScale=new Vector3(Mathf.Abs(surface.localScale.x)/1000f,Mathf.Abs(surface.localScale.y)/562.5f,Mathf.Abs(surface.localScale.x)/1000f);
        go.layer=0;
        var canvas=go.GetComponent<Canvas>(); canvas.renderMode=RenderMode.WorldSpace; canvas.worldCamera=null; canvas.enabled=true;
        var raycaster=go.GetComponent<GraphicRaycaster>(); raycaster.ignoreReversedGraphics=false; raycaster.blockingObjects=GraphicRaycaster.BlockingObjects.None;
        if(!go.GetComponent<VRCUiShape>()) go.AddComponent<VRCUiShape>();
        var box=go.GetComponent<BoxCollider>(); if(!box) box=go.AddComponent<BoxCollider>();
        box.isTrigger=false; box.center=Vector3.zero; box.size=new Vector3(1002f,564.5f,4f); box.enabled=true;
        var udon=RequireBacking(target,target.gameObject);
        var reveal=rect.Find("Reveal Playback Icon");
        if(!reveal)
        {
            var hit=Block(rect,"Reveal Playback Icon",0,0,1000f,562.5f,new Color(1,1,1,0));
            var b=hit.gameObject.AddComponent<Button>(); b.targetGraphic=hit; b.transition=Selectable.Transition.None;
            reveal=hit.transform;
        }
        var revealButton=reveal.GetComponent<Button>();
        NoNavigation(revealButton); revealButton.targetGraphic.raycastTarget=true;
        BindButton(revealButton,udon,personal?"RevealPersonalPlaybackIcon":"RevealMainPlaybackIcon");
        var icon=rect.Find("Playback Icon");
        if(!icon)
        {
            var plate=Block(rect,"Playback Icon",0,0,130,130,Color.clear);
            var b=plate.gameObject.AddComponent<Button>(); b.targetGraphic=plate; b.transition=Selectable.Transition.None;
            plate.gameObject.AddComponent<CanvasGroup>();
            var art=Rect(plate.transform,"Artwork",0,0,104,104).gameObject.AddComponent<RawImage>();
            art.uvRect=new UnityEngine.Rect(.21f,.21f,.58f,.58f); art.raycastTarget=false;
            icon=plate.transform;
        }
        var iconButton=icon.GetComponent<Button>();
        group=icon.GetComponent<CanvasGroup>(); artwork=icon.Find("Artwork").GetComponent<RawImage>();
        artwork.texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/PlayIcon.png");
        // The old white Image was the visible background, not the transparent PNG.
        icon.GetComponent<Image>().color = Color.clear;
        iconButton.transition = Selectable.Transition.None;
        artwork.raycastTarget=false; artwork.color=Color.white;
        artwork.uvRect = new UnityEngine.Rect(.20f,.20f,.60f,.60f);
        NoNavigation(iconButton); iconButton.targetGraphic.raycastTarget=true;
        BindButton(iconButton,udon,personal?"ClickPersonalPlaybackIcon":"ClickMainPlaybackIcon");
        group.alpha=0f; group.interactable=true; group.blocksRaycasts=true;
        icon.SetAsLastSibling(); icon.gameObject.SetActive(false);
        if (personal) EnsurePersonalConsole(rect, udon);
        foreach(var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer=0;
        if(sceneObject) EditorSceneManager.MarkSceneDirty(parent.gameObject.scene);
    }

    private static void EnsurePersonalConsole(RectTransform rect, UdonBehaviour udon)
    {
        if (!font) throw new InvalidOperationException("悬浮屏控制台需要支持简体中文的动态字体，请先导入字体或生成主控制台。");
        // Generated objects only: recreate the settings UI so upgrades cannot leave stale events.
        var oldGear = rect.Find("Personal Settings");
        if (oldGear) Undo.DestroyObjectImmediate(oldGear.gameObject);
        var oldPanel = rect.Find("Personal Console");
        if (oldPanel) Undo.DestroyObjectImmediate(oldPanel.gameObject);
        var hit = Block(rect,"Personal Settings",-442,224,88,88,new Color(.04f,.05f,.06f,.8f));
        var gearGroup = hit.gameObject.GetComponent<CanvasGroup>();
        if (!gearGroup) gearGroup = hit.gameObject.AddComponent<CanvasGroup>();
        gearGroup.alpha = 0f; gearGroup.interactable = false; gearGroup.blocksRaycasts = false;
        var gear = hit.gameObject.AddComponent<Button>(); gear.targetGraphic=hit; gear.transition=Selectable.Transition.None;
        var art=Rect(hit.transform,"Gear",0,0,40,40).gameObject.AddComponent<RawImage>();
        art.texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/SettingsIcon.png");
        if (!art.texture) throw new FileNotFoundException("SettingsIcon.png");
        art.raycastTarget=false; NoNavigation(gear); BindButton(gear,udon,"OpenPersonalSettings");
        hit.gameObject.SetActive(false);
        var bg=Block(rect,"Personal Console",0,0,1000,562.5f,dark); bg.raycastTarget=true;
        var panelRect=bg.rectTransform;
        Label(panelRect,"Title","极星播放器 · 个人控制台",-220,241,510,50,24,white).fontStyle=FontStyle.Bold;
        PersonalButton(panelRect,"返回播放屏幕","ClosePersonalSettings",330,239,300,white,dark,udon);
        Label(panelRect,"Status","待机中",-240,178,470,36,19,white);
        var urlBg=Block(panelRect,"URL",-240,124,470,44,panel);
        var input=urlBg.gameObject.AddComponent<VRCUrlInputField>(); input.targetGraphic=urlBg;
        var inputText=Label(urlBg.transform,"Input text","",0,0,446,40,19,white); inputText.supportRichText=false;
        var placeholder=Label(urlBg.transform,"Placeholder","请粘贴视频网址",0,0,446,40,18,muted); placeholder.supportRichText=false;
        input.textComponent=inputText; input.placeholder=placeholder; input.characterLimit=2048; input.caretColor=white; NoNavigation(input);
        PersonalButton(panelRect,"添加到列表","AddPersonalAddress",-360,61,225,white,dark,udon);
        PersonalButton(panelRect,"重置网址","ResetPersonalAddress",-120,61,225,panel,white,udon);
        PersonalButton(panelRect,"本地暂停","PersonalTogglePause",-360,0,225,panel,white,udon);
        PersonalButton(panelRect,"重新同步","PersonalResync",-120,0,225,panel,white,udon);
        Label(panelRect,"Volume title","本地音量",-375,-60,200,34,18,muted);
        PersonalSlider(panelRect,"Volume","PersonalVolumeChanged",-160,-60,300,udon);
        Label(panelRect,"Time","--:-- / --:--",-240,-110,470,32,18,white,TextAnchor.MiddleCenter);
        PersonalSlider(panelRect,"Progress","PersonalProgressChanged",-240,-153,470,udon);
        Label(panelRect,"Permissions","进度仅播放者可调整",-240,-206,470,36,17,muted,TextAnchor.MiddleCenter);
        Block(panelRect,"Divider",9,-10,2,420,panel).raycastTarget=false;
        var queue=Label(panelRect,"Queue","播放列表 (0/20)",255,21,446,348,18,white,TextAnchor.UpperLeft);
        queue.supportRichText=false;
        PersonalButton(panelRect,"上一页","PersonalPreviousPage",113,-166,148,panel,white,udon);
        Label(panelRect,"Page","1/1",255,-166,120,44,18,white,TextAnchor.MiddleCenter);
        PersonalButton(panelRect,"下一页","PersonalNextPage",398,-166,148,panel,white,udon);
        PersonalButton(panelRect,"跳过视频","PersonalSkip",140,-229,205,white,dark,udon);
        PersonalButton(panelRect,"停止并清空","PersonalStop",370,-229,225,panel,white,udon);
        bg.gameObject.SetActive(false);
        Undo.RegisterCreatedObjectUndo(hit.gameObject,"Create personal settings button");
        Undo.RegisterCreatedObjectUndo(bg.gameObject,"Create personal console");
    }
    private static void PersonalButton(Transform root,string title,string evt,float x,float y,float w,Color bg,Color fg,UdonBehaviour udon)
    {
        var button=ButtonAt(root,title,evt,x,y,w,bg,fg,udon);
        CompactButton(root,title,evt=="ClosePersonalSettings" ? 21 : 19,42f,false);
        NoNavigation(button); BindButton(button,udon,evt);
    }
    private static void PersonalSlider(Transform root,string name,string evt,float x,float y,float width,UdonBehaviour udon)
    {
        var r=Rect(root,name,x,y,width,46); var slider=r.gameObject.AddComponent<Slider>();
        Block(r,"Track",0,0,width-20,5,panel).raycastTarget=false;
        var fillArea=Rect(r,"Fill Area",0,0,width-20,5);
        var fill=Block(fillArea,"Fill",0,0,width-20,5,white); fill.raycastTarget=false;
        fill.rectTransform.anchorMin=Vector2.zero; fill.rectTransform.anchorMax=Vector2.one;
        fill.rectTransform.sizeDelta=Vector2.zero; fill.rectTransform.anchoredPosition=Vector2.zero;
        var handleArea=Rect(r,"Handle Area",0,0,width-20,40);
        var handle=Block(handleArea,"Handle",0,0,14,18,white);
        slider.fillRect=fill.rectTransform; slider.handleRect=handle.rectTransform; slider.targetGraphic=handle;
        slider.minValue=0; slider.maxValue=1; NoNavigation(slider);
        CompactSlider(slider,5f,12f,12f,false);
        BindSlider(slider,udon,evt,false);
    }

    private static void SetRendererMaterial(Renderer renderer, Material material)
    {
        if (renderer.sharedMaterial == material) return;
        Undo.RecordObject(renderer, "Link Eaststar Screen");
        renderer.sharedMaterial = material;
        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        EditorUtility.SetDirty(renderer);
        EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
    }

    private static SerializedProperty Need(SerializedObject so,string name)
    { var p=so.FindProperty(name); if(p==null) throw new Exception("SDK field not found: "+name+". Check SDK version."); return p; }
    private static RectTransform Rect(Transform p,string name,float x,float y,float w,float h)
    {
        var go=new GameObject(name,typeof(RectTransform)); var r=(RectTransform)go.transform; r.SetParent(p,false); r.anchorMin=r.anchorMax=new Vector2(.5f,.5f); r.sizeDelta=new Vector2(w,h); r.anchoredPosition=new Vector2(x,y); return r;
    }
    private static Image Block(Transform p,string name,float x,float y,float w,float h,Color color)
    { var r=Rect(p,name,x,y,w,h); var img=r.gameObject.AddComponent<Image>(); img.color=color; return img; }
    private static Text Label(Transform p,string name,string value,float x,float y,float w,float h,int size,Color color,TextAnchor alignment=TextAnchor.MiddleLeft)
    {
        var r=Rect(p,name,x,y,w,h); var t=r.gameObject.AddComponent<Text>(); t.font=font; t.text=value; t.fontSize=size; t.color=color; t.alignment=alignment; t.raycastTarget=false; t.horizontalOverflow=HorizontalWrapMode.Wrap; t.verticalOverflow=VerticalWrapMode.Truncate; return t;
    }
    private static Button ButtonAt(Transform p,string title,string evt,float x,float y,float w,Color bg,Color fg,UdonBehaviour udon)
    {
        var image=Block(p,title,x,y,w,52,bg); var b=image.gameObject.AddComponent<Button>(); b.targetGraphic=image; Label(image.transform,"Label",title,0,0,w-8,50,20,fg,TextAnchor.MiddleCenter);
        UnityEventTools.AddStringPersistentListener(b.onClick,udon.SendCustomEvent,evt);
        return b;
    }
    private static void CreateConsole(GameObject root)
    {
        var script=root.AddUdonSharpComponent<EaststarConsole>();
        script.defaultInputAddress = EditionDefaultUrl();
        var udon=RequireBacking(script, root);
        var sample = FindSdkVideoCanvas();
        // Instantiate the SDK's actual Canvas hierarchy, not a hand-built Canvas with copied scalars.
        // The clone is a plain scene object; do not retain sample player scripts or event targets.
        var canvasGO = UnityEngine.Object.Instantiate(sample.gameObject, root.transform, false);
        canvasGO.name = "Control UI";
        canvasGO.SetActive(true);
        var canvas = canvasGO.GetComponent<Canvas>();
        // The sample's child widgets are replaced by the Chinese UI below.
        for (int i = canvasGO.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(canvasGO.transform.GetChild(i).gameObject);
        foreach (var component in canvasGO.GetComponents<Component>())
        {
            if (!component || component is RectTransform || component is Canvas || component is GraphicRaycaster ||
                component is CanvasScaler || component is VRCUiShape || component is BoxCollider) continue;
            UnityEngine.Object.DestroyImmediate(component);
        }
        var rect = (RectTransform)canvasGO.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f,.5f);
        rect.localPosition = Vector3.zero;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * .0017f;
        rect.sizeDelta = new Vector2(780,580);
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGO.layer = 0;
        var bg=Block(rect,"Background",0,0,780,580,dark); bg.raycastTarget=false;
        var logo=Rect(rect,"Logo",-310,204,100,88).gameObject.AddComponent<RawImage>();
        logo.texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Root+"/Textures/ConsoleLogo.png");
        if (!logo.texture) throw new FileNotFoundException("ConsoleLogo.png is missing.");
        // Crop only empty transparent padding, not the artwork; original RGBA stays intact.
        logo.uvRect=new UnityEngine.Rect(0f, 0f, 1f, 1f);
        logo.color=Color.white; logo.raycastTarget=false;
        Label(rect,"Title","极星播放器",55,211,600,42,31,white).fontStyle=FontStyle.Bold;
        Label(rect,"Subtitle","VRCHAT  /  视频控制台",55,175,600,30,16,muted);
        script.statusText=Label(rect,"Status","待机中",0,132,704,28,19,white);
        var inputBg=Block(rect,"URL Input",0,82,704,56,panel); var input=inputBg.gameObject.AddComponent<VRCUrlInputField>(); input.targetGraphic=inputBg;
        var text=Label(inputBg.transform,"Input text","",0,0,670,52,21,white); text.supportRichText=false;
        var place=Label(inputBg.transform,"Placeholder","请粘贴视频网址",0,0,670,52,20,muted); place.supportRichText=false;
        input.textComponent=text; input.placeholder=place; input.characterLimit=0; input.caretColor=white; script.urlInput=input;
        script.prefixText=Label(rect,"Prefix",EditionHint,0,32,704,30,15,muted);
        ButtonAt(rect,"添加到列表","PlayAddress",-267,-25,170,white,dark,udon);
        script.stopButton=ButtonAt(rect,"停止并清空","StopPlayback",-89,-25,170,panel,white,udon);
        ButtonAt(rect,"重置网址","ResetAddress",89,-25,170,panel,white,udon);
        ButtonAt(rect,"重新同步","Resync",267,-25,170,panel,white,udon);
        Label(rect,"Volume title","本地音量",-248,-90,208,32,17,muted);
        var sliderRect=Rect(rect,"Volume",110,-90,484,40); var slider=sliderRect.gameObject.AddComponent<Slider>();
        Block(sliderRect,"Track",0,0,464,10,panel).raycastTarget=false;
        var fillArea=Rect(sliderRect,"Fill Area",0,0,464,10); var fill=Block(fillArea,"Fill",0,0,464,10,white); fill.raycastTarget=false;
        var handleArea=Rect(sliderRect,"Handle Area",0,0,464,34); var handle=Block(handleArea,"Handle",0,0,24,30,white);
        fill.rectTransform.anchorMin=Vector2.zero; fill.rectTransform.anchorMax=Vector2.one; fill.rectTransform.sizeDelta=Vector2.zero; fill.rectTransform.anchoredPosition=Vector2.zero;
        slider.fillRect=fill.rectTransform; slider.handleRect=handle.rectTransform; slider.targetGraphic=handle; slider.minValue=0; slider.maxValue=1; slider.value=.6f;
        UnityEventTools.AddStringPersistentListener(slider.onValueChanged,udon.SendCustomEvent,"ChangeVolume"); script.volumeSlider=slider;
        var separator=Block(rect,"Separator",0,-128,704,1,panel); separator.raycastTarget=false;
        Label(rect,"Progress title","播放进度",-250,-158,204,30,20,white).fontStyle=FontStyle.Bold;
        script.progressText=Label(rect,"Progress time","--:-- / --:--",158,-158,388,30,20,white,TextAnchor.MiddleRight);
        var progressRect=Rect(rect,"Progress",0,-196,704,44); var progress=progressRect.gameObject.AddComponent<Slider>();
        Block(progressRect,"Track",0,0,680,12,panel).raycastTarget=false;
        var progressFillArea=Rect(progressRect,"Fill Area",0,0,680,12);
        var progressFill=Block(progressFillArea,"Fill",0,0,680,12,white); progressFill.raycastTarget=false;
        progressFill.rectTransform.anchorMin=Vector2.zero; progressFill.rectTransform.anchorMax=Vector2.one;
        progressFill.rectTransform.sizeDelta=Vector2.zero; progressFill.rectTransform.anchoredPosition=Vector2.zero;
        var progressHandleArea=Rect(progressRect,"Handle Area",0,0,680,34);
        var progressHandle=Block(progressHandleArea,"Handle",0,0,24,32,white);
        progress.fillRect=progressFill.rectTransform; progress.handleRect=progressHandle.rectTransform;
        progress.targetGraphic=progressHandle; progress.minValue=0; progress.maxValue=1; progress.value=0;
        progress.interactable=false;
        UnityEventTools.AddStringPersistentListener(progress.onValueChanged,udon.SendCustomEvent,"ChangeProgress");
        script.progressSlider=progress;
        script.controllerText=Label(rect,"Controller","暂无播放者",0,-239,704,32,17,muted);
        // Preserve the known working canvas and original left-side controls. Extend it to the right.
        rect.sizeDelta = new Vector2(1540,580);
        bg.rectTransform.sizeDelta = rect.sizeDelta;
        for (int i=0; i<rect.childCount; i++)
        {
            var child = (RectTransform)rect.GetChild(i);
            if (child == bg.rectTransform) continue;
            child.anchoredPosition += new Vector2(-380,0);
        }
        Block(rect,"Queue divider",0,0,2,520,panel).raycastTarget=false;
        script.queueHeader=Label(rect,"Queue title","播放列表 (0/20)",380,245,704,38,27,white);
        script.queueHeader.fontStyle=FontStyle.Bold;
        script.nowPlayingText=Label(rect,"Now Playing","当前没有视频",380,190,704,64,17,muted);
        script.nowPlayingText.supportRichText=false;
        script.queueRows=new Text[5];
        for(int i=0;i<5;i++)
        {
            var row=Block(rect,"Queue Row "+i,380,124-i*61,704,57,panel); row.raycastTarget=false;
            var label=Label(row.transform,"Queue Item",i==0?"暂无待播放视频":"",0,0,678,55,17,white);
            label.supportRichText=false; script.queueRows[i]=label;
        }
        script.prevButton=ButtonAt(rect,"上一页","PreviousPage",125,-197,156,panel,white,udon);
        script.queuePageText=Label(rect,"Queue page","1 / 1",300,-197,170,40,17,muted,TextAnchor.MiddleCenter);
        script.nextButton=ButtonAt(rect,"下一页","NextPage",465,-197,156,panel,white,udon);
        script.skipButton=ButtonAt(rect,"跳过视频","SkipVideo",635,-197,180,white,dark,udon);
        Label(rect,"Queue permission","仅房主可跳过或停止",290,-251,500,28,16,muted);
        Label(rect,"Author","作者:miluo",628,-268,208,26,14,muted,TextAnchor.MiddleRight);
        script.skipButton.interactable=false; script.stopButton.interactable=false;
        script.prevButton.interactable=false; script.nextButton.interactable=false;
        EnsureConsolePause(script);
        ApplyVideoPlayersUI(script, sample, false);
        RequireBacking(script, root);
        EditorUtility.SetDirty(script);
    }

    // Only explicit editor actions. No InitializeOnLoad, background refresh or domain reload callback.
    // The official VideoPlayers sample is read directly from the installed SDK, not copied into this package.
    private static Canvas FindSdkVideoCanvas()
    {
        var paths = new List<string>();
        paths.Add("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VideoPlayers/UdonSyncPlayer (Unity).prefab");
        paths.Add("Packages/com.vrchat.worlds/Samples/UdonExampleScene/Prefabs/VideoPlayers/UdonSyncPlayer (AVPro).prefab");
        foreach (string path in AssetDatabase.GetAllAssetPaths())
        {
            if (path.IndexOf("/VideoPlayers/", StringComparison.OrdinalIgnoreCase) < 0 ||
                !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileNameWithoutExtension(path).StartsWith("UdonSyncPlayer", StringComparison.OrdinalIgnoreCase)) continue;
            if (!paths.Contains(path)) paths.Add(path);
        }
        foreach (string path in paths)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab) continue;
            foreach (var canvas in prefab.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas.renderMode != RenderMode.WorldSpace || !canvas.GetComponent<VRCUiShape>() ||
                    !canvas.GetComponent<GraphicRaycaster>() || !canvas.GetComponentInChildren<VRCUrlInputField>(true)) continue;
                Debug.Log("[极星播放器] 使用官方 VideoPlayers UI 设置：" + path + " / " + canvas.name, prefab);
                return canvas;
            }
        }
        throw new InvalidOperationException("未找到官方 VideoPlayers 的 World Space URL 控制台。请在 Packages/VRChat SDK - Worlds/Samples/UdonExampleScene/Prefabs/VideoPlayers 中确认 UdonSyncPlayer (Unity) 或 (AVPro) 示例存在。本次未使用猜测的备用设置。");
    }

    private static T EnsureUIComponent<T>(GameObject go, bool undo) where T : Component
    {
        var component = go.GetComponent<T>();
        return component ? component : (undo ? Undo.AddComponent<T>(go) : go.AddComponent<T>());
    }

    // Copy only scalar settings. Never import the sample's GameObject/Udon/VideoPlayer references or UnityEvents.
    private static void CopyUISettings(Component source, Component target, params string[] only)
    {
        if (!source || !target || source.GetType() != target.GetType()) return;
        var from = new SerializedObject(source);
        var to = new SerializedObject(target);
        var p = from.GetIterator();
        bool enter = true;
        while (p.NextVisible(enter))
        {
            enter = false;
            if (p.name.StartsWith("m_Object", StringComparison.Ordinal) || p.name == "m_Enabled" ||
                p.name == "m_Script" || p.name == "m_EditorHideFlags") continue;
            if (only.Length > 0 && Array.IndexOf(only, p.name) < 0) continue;
            if (p.propertyType != SerializedPropertyType.Boolean && p.propertyType != SerializedPropertyType.Integer &&
                p.propertyType != SerializedPropertyType.Float && p.propertyType != SerializedPropertyType.Enum &&
                p.propertyType != SerializedPropertyType.Vector2 && p.propertyType != SerializedPropertyType.Vector3) continue;
            var dest = to.FindProperty(p.propertyPath);
            if (dest != null && dest.propertyType == p.propertyType) to.CopyFromSerializedProperty(p);
        }
        to.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void NoNavigation(Selectable selectable)
    {
        var nav = selectable.navigation;
        nav.mode = Navigation.Mode.None;
        nav.selectOnUp = nav.selectOnDown = nav.selectOnLeft = nav.selectOnRight = null;
        selectable.navigation = nav;
    }

    private static void BindButton(Button button, UdonBehaviour udon, string eventName)
    {
        // Replace generated bindings only, avoiding stale references to previous prefab UdonBehaviour instances.
        button.onClick = new Button.ButtonClickedEvent();
        UnityEventTools.AddStringPersistentListener(button.onClick, udon.SendCustomEvent, eventName);
        button.interactable = true;
        CheckEvent(button.onClick, udon, button.name);
    }

    private static void BindSlider(Slider slider, UdonBehaviour udon, string eventName, bool record)
    {
        // A full-size hit target allows clicking the entire track, not just its handle.
        var hit = EnsureUIComponent<Image>(slider.gameObject, record);
        hit.color = Color.clear;
        hit.raycastTarget = true;
        if (slider.handleRect)
        {
            var graphic = slider.handleRect.GetComponent<Graphic>();
            if (graphic) graphic.raycastTarget = true;
            var dot = slider.handleRect.Find("Compact Thumb");
            var dotGraphic = dot ? dot.GetComponent<Graphic>() : null;
            if (dotGraphic) { dotGraphic.raycastTarget = true; slider.targetGraphic = dotGraphic; }
            else if (graphic) slider.targetGraphic = graphic;
        }
        slider.onValueChanged = new Slider.SliderEvent();
        UnityEventTools.AddStringPersistentListener(slider.onValueChanged, udon.SendCustomEvent, eventName);
        CheckEvent(slider.onValueChanged, udon, slider.name);
    }

    private static void ApplyVideoPlayersUI(EaststarConsole script, Canvas sample, bool record)
    {
        if (!script || !script.urlInput || !script.volumeSlider || !script.progressSlider)
            throw new InvalidOperationException("控制台引用缺失，请确认这是 v0.2.4 或之后的极星控制台。");
        var canvas = script.urlInput.GetComponentInParent<Canvas>(true);
        if (!canvas || !canvas.transform.IsChildOf(script.transform)) throw new InvalidOperationException("找不到控制台的 Canvas。");
        var udon = RequireBacking(script, script.gameObject);
        var buttons = new Dictionary<string, Button>();
        foreach (var button in canvas.GetComponentsInChildren<Button>(true))
            if (button.name == "添加到列表" || button.name == "停止并清空" || button.name == "重置网址" || button.name == "重新同步" || button.name == "跳过视频" || button.name == "上一页" || button.name == "下一页" || button.name == "本地暂停")
                buttons.Add(button.name, button);
        if (buttons.Count != 8) throw new InvalidOperationException("控制台缺少新版按钮，请重新生成。");
        if (record) Undo.RegisterFullObjectHierarchyUndo(script.gameObject, "Repair Eaststar VideoPlayers UI");

        var canvasGo = canvas.gameObject;
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.enabled = true;
        canvas.worldCamera = null;
        canvas.additionalShaderChannels = sample.additionalShaderChannels;
        var raycaster = EnsureUIComponent<GraphicRaycaster>(canvasGo, record);
        CopyUISettings(sample.GetComponent<GraphicRaycaster>(), raycaster);
        raycaster.enabled = true;
        // Do not let unrelated colliders block the GraphicRaycaster's own UI query.
        // The VRChat physics pointer still requires an unobstructed UIShape collider.
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        var scaler = EnsureUIComponent<CanvasScaler>(canvasGo, record);
        CopyUISettings(sample.GetComponent<CanvasScaler>(), scaler);
        scaler.enabled = true;
        var shape = EnsureUIComponent<VRCUiShape>(canvasGo, record);
        CopyUISettings(sample.GetComponent<VRCUiShape>(), shape);
        // Some SDK versions expose the UIShape as a Behaviour, others as a wrapper.
        var shapeBehaviour = (Component)shape as Behaviour;
        if (shapeBehaviour) shapeBehaviour.enabled = true;
        var collider = EnsureUIComponent<BoxCollider>(canvasGo, record);
        // Both positive and negative faces of the UI plane lie inside the collider.
        // A centered collider avoids the old near-zero margin on one face (ClientSim checks containment).
        var bounds = ((RectTransform)canvas.transform).rect;
        collider.isTrigger = false;
        collider.size = new Vector3(bounds.width + 2f, bounds.height + 2f, 12f);
        collider.center = new Vector3(bounds.center.x, bounds.center.y, 0f);
        collider.enabled = true;
        collider.sharedMaterial = null;
        foreach (var tr in script.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = 0;
        raycaster.ignoreReversedGraphics = false;

        // Only interactive graphics receive raycasts; decorative art/text can never steal clicks.
        foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
        foreach (var group in canvas.GetComponentsInChildren<CanvasGroup>(true))
        { group.interactable = true; group.blocksRaycasts = true; }
        foreach (var selectable in canvas.GetComponentsInChildren<Selectable>(true))
        {
            NoNavigation(selectable);
            if (selectable.targetGraphic) { selectable.targetGraphic.raycastTarget = true; selectable.targetGraphic.enabled = true; }
        }
        var input = script.urlInput;
        var sampleInput = sample.GetComponentInChildren<VRCUrlInputField>(true);
        CopyUISettings(sampleInput, input, "m_ContentType", "m_InputType", "m_KeyboardType", "m_CharacterValidation", "m_LineType", "m_HideMobileInput");
        input.characterLimit = 0;
        input.readOnly = false;
        input.interactable = true;
        input.enabled = true;
        if (!input.targetGraphic) input.targetGraphic = EnsureUIComponent<Image>(input.gameObject, record);
        input.targetGraphic.raycastTarget = true;
        if (input.textComponent) input.textComponent.raycastTarget = false;
        if (input.placeholder) input.placeholder.raycastTarget = false;
        BindButton(buttons["添加到列表"], udon, "PlayAddress");
        BindButton(buttons["停止并清空"], udon, "StopPlayback");
        BindButton(buttons["跳过视频"], udon, "SkipVideo");
        BindButton(buttons["上一页"], udon, "PreviousPage");
        BindButton(buttons["下一页"], udon, "NextPage");
        BindButton(buttons["重置网址"], udon, "ResetAddress");
        BindButton(buttons["重新同步"], udon, "Resync");
        BindButton(buttons["本地暂停"], udon, "ToggleLocalPause");
        BindSlider(script.volumeSlider, udon, "ChangeVolume", record);
        BindSlider(script.progressSlider, udon, "ChangeProgress", record);
        script.volumeSlider.interactable = true;
        // The original Udon runtime still controls seek permissions. Do not unlock it in the editor.
        script.progressSlider.interactable = false;
        if (record)
        {
            foreach (var tr in canvas.GetComponentsInChildren<Transform>(true))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(tr.gameObject);
                foreach (var component in tr.GetComponents<Component>())
                {
                    if (!component) continue;
                    EditorUtility.SetDirty(component);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
            }
            EditorSceneManager.MarkSceneDirty(script.gameObject.scene);
        }
        Debug.Log("[极星播放器] UIShape、碰撞体、Raycaster、URL 输入框及 10 个 Udon 事件绑定已检查。", script);
    }

    private static void EnsureSceneEventSystem(Scene scene)
    {
        // An existing active EventSystem is reused. Do not modify unrelated input modules.
        foreach (var root in scene.GetRootGameObjects())
            foreach (var existing in root.GetComponentsInChildren<EventSystem>(true))
            {
                if (!existing.isActiveAndEnabled) continue;
                bool hasModule = false;
                foreach (var module in existing.GetComponents<BaseInputModule>())
                    if (module.isActiveAndEnabled) hasModule = true;
                if (!hasModule) Undo.AddComponent<StandaloneInputModule>(existing.gameObject);
                return;
            }
        var go = new GameObject("EventSystem (Eaststar)", typeof(EventSystem), typeof(StandaloneInputModule));
        if (go.scene != scene) SceneManager.MoveGameObjectToScene(go, scene);
        Undo.RegisterCreatedObjectUndo(go, "Add Eaststar EventSystem");
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void ValidateInteraction(EaststarConsole script)
    {
        var canvas = script.urlInput.GetComponentInParent<Canvas>(true);
        if (!canvas || canvas.renderMode != RenderMode.WorldSpace || canvas.gameObject.layer != 0 ||
            !canvas.GetComponent<VRCUiShape>() || !canvas.GetComponent<GraphicRaycaster>())
            throw new InvalidOperationException("Canvas/VRCUiShape/GraphicRaycaster/Layer 配置无效。");
        var collider = canvas.GetComponent<BoxCollider>();
        var rect = (RectTransform)canvas.transform;
        if (!collider || !collider.enabled || collider.isTrigger || collider.size.z <= 0 ||
            Mathf.Abs(collider.center.z) >= collider.size.z * .5f)
            throw new InvalidOperationException("UIShape 碰撞体未覆盖 Canvas 平面。");
        foreach (var selectable in canvas.GetComponentsInChildren<Selectable>(true))
        {
            if (!selectable.targetGraphic || !selectable.targetGraphic.raycastTarget)
                throw new InvalidOperationException("交互图形没有 Raycast Target：" + selectable.name);
            var corners = new Vector3[4];
            ((RectTransform)selectable.transform).GetWorldCorners(corners);
            foreach (var world in corners)
            {
                var local = rect.InverseTransformPoint(world) - collider.center;
                var half = collider.size * .5f;
                if (Mathf.Abs(local.x) > half.x + .1f || Mathf.Abs(local.y) > half.y + .1f || Mathf.Abs(local.z) > half.z + .1f)
                    throw new InvalidOperationException("控件超出 UIShape 碰撞体：" + selectable.name);
            }
        }
    }

    [MenuItem("Tools/Eaststar Player/5 - Replace Selected Console With New Prefab")]
    public static void ReplaceSelectedConsole()
    {
        if (generating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || !ResolveRoot()) return;
        var selected = Selection.activeGameObject;
        var old = selected && !EditorUtility.IsPersistent(selected) ? selected.GetComponentInParent<EaststarConsole>(true) : null;
        if (!old || PrefabStageUtility.GetCurrentPrefabStage() != null)
        { Debug.LogWarning("[极星播放器] 请在场景中选中旧控制台，而非 Project 中的预制件。"); return; }
        try
        {
            var path = Prefabs + "/Eaststar_Console.prefab";
            ValidatePrefabs(Prefabs + "/Eaststar_Display.prefab", path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Replace Eaststar Console");
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, old.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(go, "New Eaststar Console");
                go.transform.SetParent(old.transform.parent, false);
                go.transform.localPosition = old.transform.localPosition;
                go.transform.localRotation = old.transform.localRotation;
                go.transform.localScale = old.transform.localScale;
                go.transform.SetSiblingIndex(old.transform.GetSiblingIndex());
                var fresh = go.GetComponent<EaststarConsole>();
                var serialized = new SerializedObject(fresh);
                var previous = new SerializedObject(old);
                serialized.CopyFromSerializedProperty(Need(previous,"screen"));
                serialized.ApplyModifiedProperties();
                // Do not carry over the other edition's URL preset.
                fresh.defaultInputAddress = EditionDefaultUrl();
                PrefabUtility.RecordPrefabInstancePropertyModifications(fresh);
                EditorUtility.SetDirty(fresh);
                ValidateInteraction(fresh);
                EnsureSceneEventSystem(go.scene);
                // Remove old instance only after validation and transfer of the scene settings.
                Undo.DestroyObjectImmediate(old.gameObject);
                EditorSceneManager.MarkSceneDirty(go.scene);
                Selection.activeGameObject = go;
                Debug.Log("[极星播放器 v0.3.13] 已换成新控制台实例，保留位置、旋转、缩放、主屏引用，并应用本版本解析设置。请保存场景。",go);
                Undo.CollapseUndoOperations(group);
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
        }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    [MenuItem("Tools/Eaststar Player/6 - Diagnose Console Interaction")]
    public static void DiagnoseConsole()
    {
        var selected = Selection.activeGameObject;
        var script = selected ? selected.GetComponentInParent<EaststarConsole>(true) : null;
        if (!script || EditorUtility.IsPersistent(script)) { Debug.LogWarning("请在场景中选中极星控制台。"); return; }
        try
        {
            ValidateInteraction(script);
            var canvas = script.urlInput.GetComponentInParent<Canvas>(true);
            var box = canvas.GetComponent<BoxCollider>();
            var front = -canvas.transform.forward;
            Physics.SyncTransforms();
            int blocked = 0;
            foreach (var control in canvas.GetComponentsInChildren<Selectable>(true))
            {
                var r = (RectTransform)control.transform;
                var point = r.TransformPoint(r.rect.center);
                var hits = Physics.RaycastAll(point + front * 1f, -front, 1.05f, ~0, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a,b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    if (hit.collider == box) break;
                    if (hit.collider.gameObject.layer == 2) continue;
                    Debug.LogWarning("[极星播放器] 正面射线可能被遮挡：" + control.name + " / " + hit.collider.name, hit.collider);
                    blocked++; break;
                }
            }
            bool hasEvent = false;
            foreach(var root in script.gameObject.scene.GetRootGameObjects())
                foreach(var system in root.GetComponentsInChildren<EventSystem>(true))
                    if(system.isActiveAndEnabled) hasEvent = true;
            if (!hasEvent) Debug.LogWarning("[极星播放器] 场景没有启用的 EventSystem。");
            Debug.Log("[极星播放器] 本地静态检查完成；潜在遮挡数：" + blocked + "；EventSystem：" + hasEvent + "。请从 Canvas 的 -Z 正面，在 ClientSim/Build & Test 点击添加到列表、输入框或分页按钮。", script);
        }
        catch(Exception ex) { Debug.LogException(ex); }
    }

}

// Official SDK callback; runs before building worlds, never when building avatars.
public class EaststarScreenBuildLinker : IVRCSDKBuildRequestedCallback
{
    public int callbackOrder => -10000;
    public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
    {
        if (requestedBuildType != VRCSDKRequestedBuildType.Scene) return true;
        return EaststarBuilder.LinkScreens(false);
    }
}
