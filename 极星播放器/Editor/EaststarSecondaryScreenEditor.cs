using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UdonSharpEditor;

// Editor-only positioning tool. No update loop, build callback or runtime physics components.
[CustomEditor(typeof(EaststarSecondaryScreen))]
public sealed class EaststarSecondaryScreenEditor : Editor
{
    private bool ownsToolVisibility, previousToolVisibility, dragging;
    private Vector3 rawPosition, startUp, startFront;
    private Quaternion startRotation, handleRotation;
    private bool hasHit;
    private Vector3 hitPoint;
    private readonly List<MeshFilter> meshFilters = new List<MeshFilter>();
    private readonly List<Collider> colliders = new List<Collider>();
    private readonly Dictionary<Mesh, MeshGeometry> meshCache = new Dictionary<Mesh, MeshGeometry>();

    private sealed class MeshGeometry
    {
        public Vector3[] vertices;
        public int[] indices;
    }

    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
        serializedObject.Update();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("mainScreenId"));
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("副屏表面吸附（仅编辑器）", EditorStyles.boldLabel);
        var enabled = serializedObject.FindProperty("snapToSurface");
        EditorGUILayout.PropertyField(enabled, new GUIContent("吸附面", "开启后，在 Scene 中使用 W 移动工具拖动副屏。"));
        using (new EditorGUI.DisabledScope(!enabled.boolValue))
        {
            var distance = serializedObject.FindProperty("snapDistance");
            distance.floatValue = Mathf.Max(0.01f, EditorGUILayout.FloatField(new GUIContent("吸附距离（米）"), distance.floatValue));
            var offset = serializedObject.FindProperty("snapOffset");
            offset.floatValue = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("离面间隙（米）"), offset.floatValue));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("snapAlignNormal"), new GUIContent("自动贴合朝向"));
        }
        if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();
        var screen = (EaststarSecondaryScreen)target;
        if (!screen.snapToSurface) RestoreTool();
        EditorGUILayout.HelpBox("开启后选中场景中的副屏根物体，按 W 并拖动三轴手柄。进入吸附距离后自动贴面，拖出范围可自由移动。关闭开关可恢复普通移动。支持静态 Mesh 和常见 Collider；不在游戏中运行。", MessageType.Info);
        using (new EditorGUI.DisabledScope(!CanEdit(screen) || !screen.snapToSurface))
        {
            if (GUILayout.Button("立即吸附最近的面"))
            {
                BeginDrag(screen.transform);
                ApplyPosition(screen, screen.transform.position);
                EndDrag();
                SceneView.RepaintAll();
            }
        }
    }

    private static bool CanEdit(EaststarSecondaryScreen screen)
    {
        return screen && !EditorUtility.IsPersistent(screen) && screen.gameObject.scene.IsValid() &&
               screen.gameObject.scene.isLoaded && !EditorApplication.isPlayingOrWillChangePlaymode &&
               !EditorApplication.isCompiling && PrefabStageUtility.GetCurrentPrefabStage() == null;
    }

    private void OnSceneGUI()
    {
        var screen = target as EaststarSecondaryScreen;
        if (!CanEdit(screen) || !screen.snapToSurface || Selection.activeGameObject != screen.gameObject ||
            Selection.gameObjects.Length != 1 || Tools.current != Tool.Move)
        {
            RestoreTool();
            return;
        }
        if (!ownsToolVisibility)
        {
            previousToolVisibility = Tools.hidden;
            Tools.hidden = true;
            ownsToolVisibility = true;
        }
        var tr = screen.transform;
        if (!dragging)
        {
            rawPosition = tr.position;
            handleRotation = Tools.pivotRotation == PivotRotation.Local ? tr.rotation : Quaternion.identity;
        }
        EventType rawType = Event.current.rawType;
        EditorGUI.BeginChangeCheck();
        Vector3 proposed = Handles.PositionHandle(rawPosition, handleRotation);
        if (EditorGUI.EndChangeCheck())
        {
            if (!dragging) BeginDrag(tr);
            rawPosition = proposed;
            ApplyPosition(screen, proposed);
        }
        if (dragging && hasHit && Event.current.type == EventType.Repaint)
        {
            Color old = Handles.color;
            Handles.color = new Color(0.25f, 0.9f, 0.65f, 1f);
            Handles.DrawDottedLine(rawPosition, hitPoint, 4f);
            Handles.Label(tr.position + tr.up * HandleUtility.GetHandleSize(tr.position) * 0.15f, "已吸附");
            Handles.color = old;
        }
        if (dragging && (rawType == EventType.MouseUp || GUIUtility.hotControl == 0)) EndDrag();
    }

    private void OnDisable() { RestoreTool(); }
    private void RestoreTool()
    {
        if (ownsToolVisibility)
        {
            Tools.hidden = previousToolVisibility;
            ownsToolVisibility = false;
        }
        EndDrag();
    }
    private void BeginDrag(Transform tr)
    {
        dragging = true;
        startRotation = tr.rotation;
        startUp = tr.up;
        startFront = -tr.forward; // Eaststar's Quad faces local -Z.
        hasHit = false;
        meshCache.Clear();
        meshFilters.Clear();
        colliders.Clear();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (!filter || !filter.sharedMesh || Excluded(filter.gameObject, tr)) continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer && renderer.enabled) meshFilters.Add(filter);
                }
                foreach (var collider in root.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider || !collider.enabled || collider.isTrigger || Excluded(collider.gameObject, tr)) continue;
                    // Prefer the visible surface when the same object has a rendered mesh.
                    var renderer = collider.GetComponent<MeshRenderer>();
                    var filter = collider.GetComponent<MeshFilter>();
                    if (renderer && renderer.enabled && filter && filter.sharedMesh) continue;
                    colliders.Add(collider);
                }
            }
        }
        // Collider queries need up-to-date transforms; no changes to physics settings.
        Physics.SyncTransforms();
    }
    private static bool Excluded(GameObject go, Transform own)
    {
        return go.transform == own || go.transform.IsChildOf(own) ||
            go.GetComponentInParent<EaststarScreen>(true) || go.GetComponentInParent<EaststarSecondaryScreen>(true) ||
            go.GetComponentInParent<EaststarConsole>(true) || SceneVisibilityManager.instance.IsHidden(go);
    }
    private void EndDrag()
    {
        dragging = false;
        hasHit = false;
        meshFilters.Clear();
        colliders.Clear();
        meshCache.Clear();
    }
    private void ApplyPosition(EaststarSecondaryScreen screen, Vector3 requested)
    {
        var tr = screen.transform;
        Vector3 point, normal;
        hasHit = FindSurface(requested, Mathf.Max(.01f, screen.snapDistance), out point, out normal);
        hitPoint = point;
        Quaternion rotation = startRotation;
        Vector3 position = requested;
        if (hasHit)
        {
            if (screen.snapAlignNormal)
            {
                Vector3 up = Vector3.ProjectOnPlane(startUp, normal);
                if (up.sqrMagnitude < .0001f) up = Vector3.ProjectOnPlane(Vector3.forward, normal);
                if (up.sqrMagnitude < .0001f) up = Vector3.ProjectOnPlane(Vector3.right, normal);
                rotation = Quaternion.LookRotation(-normal, up.normalized);
            }
            // The black rear plane is +0.004 local metres from the root.
            float depth = .004f * Mathf.Abs(tr.lossyScale.z);
            position = point + normal * (Mathf.Max(0f, screen.snapOffset) + depth);
        }
        if ((tr.position - position).sqrMagnitude < 1e-12f && Quaternion.Angle(tr.rotation, rotation) < .001f) return;
        Undo.RecordObject(tr, "移动并吸附极星副屏");
        tr.SetPositionAndRotation(position, rotation);
        PrefabUtility.RecordPrefabInstancePropertyModifications(tr);
        EditorSceneManager.MarkSceneDirty(tr.gameObject.scene);
    }

    private bool FindSurface(Vector3 p, float distance, out Vector3 point, out Vector3 normal)
    {
        float best = distance * distance;
        point = p;
        normal = startFront;
        bool found = false;
        foreach (var f in meshFilters)
        {
            if (!f || !f.sharedMesh) continue;
            var r = f.GetComponent<MeshRenderer>();
            if (!r || !r.enabled || r.bounds.SqrDistance(p) > best) continue;
            TestMesh(f.sharedMesh, f.transform.localToWorldMatrix, p, ref best, ref point, ref normal, ref found);
        }
        foreach (var c in colliders)
        {
            if (!c || !c.enabled || c.bounds.SqrDistance(p) > best) continue;
            var mc = c as MeshCollider;
            if (mc && mc.sharedMesh && !mc.convex)
            {
                TestMesh(mc.sharedMesh, mc.transform.localToWorldMatrix, p, ref best, ref point, ref normal, ref found);
                continue;
            }
            // ClosestPoint is supported for primitive and convex mesh colliders.
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider || (mc && mc.convex))
            {
                Vector3 q = c.ClosestPoint(p);
                Vector3 delta = p - q;
                if (delta.sqrMagnitude > 1e-12f)
                    Consider(p, q, delta.normalized, ref best, ref point, ref normal, ref found);
            }
            // Raycasts also cover terrain and points already inside a primitive collider.
            float reach = distance + .002f;
            TestColliderRay(c, p, Vector3.right, reach, ref best, ref point, ref normal, ref found);
            TestColliderRay(c, p, Vector3.left, reach, ref best, ref point, ref normal, ref found);
            TestColliderRay(c, p, Vector3.up, reach, ref best, ref point, ref normal, ref found);
            TestColliderRay(c, p, Vector3.down, reach, ref best, ref point, ref normal, ref found);
            TestColliderRay(c, p, Vector3.forward, reach, ref best, ref point, ref normal, ref found);
            TestColliderRay(c, p, Vector3.back, reach, ref best, ref point, ref normal, ref found);
        }
        return found;
    }
    private void TestColliderRay(Collider c, Vector3 p, Vector3 direction, float reach,
        ref float best, ref Vector3 point, ref Vector3 normal, ref bool found)
    {
        RaycastHit hit;
        if (c.Raycast(new Ray(p + direction * reach, -direction), out hit, reach * 2f))
            Consider(p, hit.point, hit.normal, ref best, ref point, ref normal, ref found);
    }
    private void TestMesh(Mesh mesh, Matrix4x4 matrix, Vector3 p, ref float best,
        ref Vector3 point, ref Vector3 normal, ref bool found)
    {
        MeshGeometry geometry;
        if (!meshCache.TryGetValue(mesh, out geometry))
        {
            try { geometry = new MeshGeometry { vertices = mesh.vertices, indices = mesh.triangles }; }
            catch (System.Exception) { geometry = null; }
            meshCache[mesh] = geometry;
        }
        if (geometry == null) return;
        var v = geometry.vertices;
        var t = geometry.indices;
        // World-space closest point avoids incorrect distances on nonuniformly scaled walls.
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 a = matrix.MultiplyPoint3x4(v[t[i]]), b = matrix.MultiplyPoint3x4(v[t[i+1]]), c = matrix.MultiplyPoint3x4(v[t[i+2]]);
            Vector3 min = Vector3.Min(a, Vector3.Min(b,c)), max = Vector3.Max(a, Vector3.Max(b,c));
            Vector3 clamped = new Vector3(Mathf.Clamp(p.x,min.x,max.x), Mathf.Clamp(p.y,min.y,max.y), Mathf.Clamp(p.z,min.z,max.z));
            if ((clamped-p).sqrMagnitude > best) continue;
            Vector3 n = Vector3.Cross(b-a,c-a);
            if (n.sqrMagnitude < 1e-16f) continue;
            Vector3 q = ClosestTriangle(p,a,b,c);
            Consider(p,q,n.normalized,ref best,ref point,ref normal,ref found);
        }
    }
    private void Consider(Vector3 p, Vector3 q, Vector3 n, ref float best,
        ref Vector3 point, ref Vector3 normal, ref bool found)
    {
        float d = (p-q).sqrMagnitude;
        if (d > best || n.sqrMagnitude < .5f) return;
        float side = Vector3.Dot(p-q,n);
        if (side < -.0001f || (Mathf.Abs(side) <= .0001f && Vector3.Dot(startFront,n) < 0f)) n = -n;
        if (found && Mathf.Abs(d-best) < 1e-8f && Vector3.Dot(startFront,n) <= Vector3.Dot(startFront,normal)) return;
        best = d; point = q; normal = n; found = true;
    }
    private static Vector3 ClosestTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab=b-a, ac=c-a, ap=p-a;
        float d1=Vector3.Dot(ab,ap), d2=Vector3.Dot(ac,ap);
        if(d1<=0f && d2<=0f) return a;
        Vector3 bp=p-b;
        float d3=Vector3.Dot(ab,bp), d4=Vector3.Dot(ac,bp);
        if(d3>=0f && d4<=d3) return b;
        float vc=d1*d4-d3*d2;
        if(vc<=0f && d1>=0f && d3<=0f) return a+ab*(d1/(d1-d3));
        Vector3 cp=p-c;
        float d5=Vector3.Dot(ab,cp), d6=Vector3.Dot(ac,cp);
        if(d6>=0f && d5<=d6) return c;
        float vb=d5*d2-d1*d6;
        if(vb<=0f && d2>=0f && d6<=0f) return a+ac*(d2/(d2-d6));
        float va=d3*d6-d5*d4;
        if(va<=0f && d4-d3>=0f && d5-d6>=0f) return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
        float denom=1f/(va+vb+vc);
        return a+ab*(vb*denom)+ac*(vc*denom);
    }
}
