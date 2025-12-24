using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.Linq;

namespace EditorTools
{
    /// <summary>
    /// 游戏对象同名嵌套检测工具
    /// 检测预制体或Hierarchy中的游戏对象是否存在父子级同名的嵌套
    /// </summary>
    public class PrefabNestedNameChecker : EditorWindow
    {
        private List<GameObject> targetObjects = new List<GameObject>();
        private Vector2 scrollPosition;
        private Vector2 targetsScrollPosition;
        private List<NestedNameInfo> nestedNameList = new List<NestedNameInfo>();
        private GUIStyle boxStyle;
        private GUIStyle labelStyle;
        private GUIStyle buttonStyle;
        private bool stylesInitialized;

        private bool showTargetList = true;
        private const int TargetItemsPerPage = 200;
        private int targetPageIndex;

        private bool showPrefabResults = true;
        private bool showSceneResults = true;
        private const int PrefabGroupsPerPage = 60;
        private const int SceneResultsPerPage = 120;
        private int prefabResultPageIndex;
        private int sceneResultPageIndex;

        private bool resultsCacheDirty = true;
        private int prefabIssueCountCache;
        private readonly List<PrefabGroupCache> prefabGroupsCache = new List<PrefabGroupCache>();
        private readonly List<NestedNameInfo> sceneEntriesCache = new List<NestedNameInfo>();
        private readonly Dictionary<string, bool> prefabFoldoutStates = new Dictionary<string, bool>();

        private class NestedNameInfo
        {
            public string path;           // 层级路径，如 "Weapon/Weapon"
            public GameObject parentObj;  // 父对象
            public GameObject childObj;   // 子对象
            public string fullPath;       // 完整路径
            public string objectPath;     // 对象路径（预制体路径或场景路径）
            public string parentPath;     // 父对象路径
            public string childPath;      // 子对象路径
            public bool isPrefab;         // 是否是预制体

            public NestedNameInfo(string path, GameObject parent, GameObject child, string fullPath, string objectPath, string parentPath, string childPath, bool isPrefab)
            {
                this.path = path;
                this.parentObj = parent;
                this.childObj = child;
                this.fullPath = fullPath;
                this.objectPath = objectPath;
                this.parentPath = parentPath;
                this.childPath = childPath;
                this.isPrefab = isPrefab;
            }
        }

        private class PrefabGroupCache
        {
            public string prefabPath;
            public string prefabName;
            public List<NestedNameInfo> entries;
        }

        [MenuItem("Tools/游戏对象同名嵌套检测")]
        public static void ShowWindow()
        {
            PrefabNestedNameChecker window = GetWindow<PrefabNestedNameChecker>("同名嵌套检测");
            window.minSize = new Vector2(500, 400);
            window.Show();
        }

        private void OnEnable()
        {
            stylesInitialized = false;
        }

        private void EnsureStyles()
        {
            if (stylesInitialized)
                return;

            boxStyle = new GUIStyle(EditorStyles.helpBox);
            labelStyle = new GUIStyle(EditorStyles.wordWrappedLabel);
            buttonStyle = new GUIStyle(EditorStyles.miniButton);
            buttonStyle.alignment = TextAnchor.MiddleLeft;

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            EnsureStyles();
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("游戏对象同名嵌套检测工具", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("拖入一个或多个预制体或Hierarchy中的游戏对象，检测是否存在父子级同名的嵌套（如 Weapon/Weapon）。可混合选择。", MessageType.Info);
            EditorGUILayout.Space(5);

            // 拖拽区域
            Rect dropArea = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "拖拽一个或多个对象到此处", EditorStyles.helpBox);

            // 处理拖拽
            HandleDragAndDrop(dropArea);

            // 清除全部按钮
            if (GUILayout.Button("清除全部", GUILayout.Width(80)))
            {
                targetObjects.Clear();
                ClearDetectionResults();
            }

            // 目标对象列表
            if (targetObjects.Count > 0)
            {
                EditorGUILayout.Space(5);
                DrawTargetObjectsList();
            }

            if (targetObjects.Count > 0)
            {
                EditorGUILayout.Space(5);
                if (GUILayout.Button("开始检测", GUILayout.Height(30)))
                {
                    CheckNestedNames();
                }
            }

            EditorGUILayout.Space(10);

            // 显示检测结果标题
            EditorGUILayout.BeginHorizontal();
            int prefabIssueCount = GetPrefabIssueCount();
            EditorGUILayout.LabelField($"检测结果: {prefabIssueCount} 个预制体存在同名嵌套", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // 显示检测结果
            if (nestedNameList.Count > 0)
            {
                // 使用ExpandHeight确保ScrollView占据剩余空间
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, false, true, GUILayout.ExpandHeight(true));

                DrawDetectionResults();

                EditorGUILayout.EndScrollView();
            }
            else if (targetObjects.Count > 0)
            {
                EditorGUILayout.HelpBox("未检测到同名嵌套的游戏对象", MessageType.Info);
            }
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropArea.Contains(evt.mousePosition))
                        return;

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();

                        foreach (Object draggedObject in DragAndDrop.objectReferences)
                        {
                            if (draggedObject is GameObject gameObject)
                            {
                                TryAddTargetObject(gameObject);
                            }
                            else if (draggedObject is DefaultAsset defaultAsset)
                            {
                                // 检测是否为目录
                                string assetPath = AssetDatabase.GetAssetPath(defaultAsset);
                                if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath))
                                {
                                    // 加载目录下所有预制体
                                    LoadPrefabsFromDirectory(assetPath);
                                }
                            }
                        }
                        Repaint();
                    }
                    break;
            }
        }

        private void LoadPrefabsFromDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || !AssetDatabase.IsValidFolder(directoryPath))
                return;

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { directoryPath });
            int total = prefabGuids.Length;
            int loadedCount = 0;

            try
            {
                for (int i = 0; i < total; i++)
                {
                    string guid = prefabGuids[i];
                    string prefabPath = AssetDatabase.GUIDToAssetPath(guid);

                    float progress = total == 0 ? 1f : (float)(i + 1) / total;
                    string progressText = $"正在加载目录 {directoryPath} 中的预制体 ({i + 1}/{total})";
                    EditorUtility.DisplayProgressBar("加载预制体", progressText, progress);

                    if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".prefab"))
                    {
                        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (prefab != null)
                        {
                            TryAddTargetObject(prefab);
                            loadedCount++;
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (loadedCount > 0)
            {
                Debug.Log($"从目录 {directoryPath} 加载了 {loadedCount} 个预制体");
            }
        }

        private void TryAddTargetObject(GameObject gameObject)
        {
            if (gameObject == null)
                return;
            if (!targetObjects.Contains(gameObject))
            {
                targetObjects.Add(gameObject);
                ClearDetectionResults();
            }
        }

        private void ClearDetectionResults()
        {
            nestedNameList.Clear();
            InvalidateResultCaches();
        }

        private string GetObjectTypeLabel(GameObject go)
        {
            if (go == null) return "";
            string assetPath = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab"))
            {
                return "预制体资源";
            }
            return "场景对象/实例";
        }

        private void DrawTargetObjectsList()
        {
            showTargetList = EditorGUILayout.Foldout(showTargetList, $"目标对象列表（{targetObjects.Count}）", true);
            if (!showTargetList)
                return;

            int totalPages = Mathf.Max(1, Mathf.CeilToInt(targetObjects.Count / (float)TargetItemsPerPage));
            DrawPaginationControls(ref targetPageIndex, totalPages, $"第 {targetPageIndex + 1}/{totalPages} 页（每页 {TargetItemsPerPage} 个）");

            int startIndex = targetPageIndex * TargetItemsPerPage;
            int endIndex = Mathf.Min(startIndex + TargetItemsPerPage, targetObjects.Count);

            targetsScrollPosition = EditorGUILayout.BeginScrollView(targetsScrollPosition, GUILayout.Height(220));
            for (int i = startIndex; i < endIndex; i++)
            {
                var obj = targetObjects[i];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(obj, typeof(GameObject), true, GUILayout.ExpandWidth(true));

                string typeLabel = GetObjectTypeLabel(obj);
                EditorGUILayout.LabelField(typeLabel, EditorStyles.miniLabel, GUILayout.Width(110));
                if (GUILayout.Button("移除", GUILayout.Width(60)))
                {
                    targetObjects.RemoveAt(i);
                    ClearDetectionResults();
                    i--;
                    endIndex = Mathf.Min(startIndex + TargetItemsPerPage, targetObjects.Count);
                    continue;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (totalPages > 1)
            {
                EditorGUILayout.HelpBox($"列表内容按分页显示以提升性能（共 {targetObjects.Count} 个对象）。", MessageType.None);
            }
        }

        private void CheckNestedNames()
        {
            ClearDetectionResults();

            if (targetObjects == null || targetObjects.Count == 0)
            {
                EditorUtility.DisplayDialog("错误", "请先添加至少一个游戏对象", "确定");
                return;
            }

            int totalIssues = 0;
            List<string> perObjectSummaries = new List<string>();
            int totalCount = targetObjects.Count;
            int currentIndex = 0;

            try
            {
                foreach (var target in targetObjects)
                {
                    if (target == null)
                    {
                        currentIndex++;
                        continue;
                    }

                    currentIndex++;
                    string assetPath = AssetDatabase.GetAssetPath(target);
                    bool isPrefab = !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab");
                    string objectPath = isPrefab ? assetPath : (target.scene.IsValid() ? (string.IsNullOrEmpty(target.scene.path) ? target.scene.name : target.scene.path) : "当前场景");
                    string objectName = isPrefab ? System.IO.Path.GetFileNameWithoutExtension(assetPath) : target.name;
                    string objectType = isPrefab ? "预制体" : "游戏对象";

                    // 更新进度条
                    float progress = (float)currentIndex / totalCount;
                    string progressText = $"正在检测 {objectType}: {objectName} ({currentIndex}/{totalCount})";
                    EditorUtility.DisplayProgressBar("检测同名嵌套", progressText, progress);

                    int beforeCount = nestedNameList.Count;
                    CheckNestedNamesForObject(target);
                    int afterCount = nestedNameList.Count;
                    int delta = afterCount - beforeCount;
                    totalIssues += delta;

                    perObjectSummaries.Add($"{objectType} {objectPath} -> {delta} 处");
                }

                // 按路径统一排序
                nestedNameList = nestedNameList.OrderBy(x => x.fullPath).ToList();

                if (totalIssues > 0)
                {
                    Debug.Log($"批量检测完成，共发现 {totalIssues} 处同名嵌套。\n" + string.Join("\n", perObjectSummaries));
                }
                else
                {
                    Debug.Log("批量检测完成，未发现同名嵌套。\n" + string.Join("\n", perObjectSummaries));
                }
            }
            finally
            {
                // 确保清除进度条
                EditorUtility.ClearProgressBar();
            }

            Repaint();
        }

        private void CheckNestedNamesForObject(GameObject targetObject)
        {
            GameObject root = targetObject;
            string objectPath = "";
            bool isPrefab = false;

            // 检查是否是预制体资源
            string assetPath = AssetDatabase.GetAssetPath(root);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab"))
            {
                // 预制体资源，需要加载内容
                isPrefab = true;
                objectPath = assetPath;

                GameObject prefabContents = null;
                try
                {
                    // 加载预制体内容以便访问其层级结构
                    prefabContents = PrefabUtility.LoadPrefabContents(assetPath);
                    if (prefabContents == null)
                    {
                        Debug.LogWarning($"跳过预制体 {assetPath}：无法加载预制体内容");
                        return;
                    }

                    root = prefabContents;

                    // 检查Transform是否存在
                    if (root.transform == null)
                    {
                        Debug.LogWarning($"跳过预制体 {assetPath}：无法访问预制体的Transform层级");
                        return;
                    }

                    // 递归检查所有子对象
                    CheckRecursive(root, root.transform, root.name, objectPath, isPrefab);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"跳过预制体 {assetPath}：{e.Message}");
                    return;
                }
                finally
                {
                    if (prefabContents != null)
                    {
                        PrefabUtility.UnloadPrefabContents(prefabContents);
                    }
                }
            }
            else
            {
                // Hierarchy中的游戏对象，直接检测
                isPrefab = false;

                // 获取场景路径
                UnityEngine.SceneManagement.Scene scene = root.scene;
                if (scene.IsValid())
                {
                    objectPath = scene.path;
                    if (string.IsNullOrEmpty(objectPath))
                    {
                        objectPath = scene.name;
                    }
                }
                else
                {
                    objectPath = "当前场景";
                }

                // 检查Transform是否存在
                if (root.transform == null)
                {
                    EditorUtility.DisplayDialog("错误", "无法访问游戏对象的Transform层级", "确定");
                    return;
                }

                // 递归检查所有子对象
                CheckRecursive(root, root.transform, root.name, objectPath, isPrefab);
            }
        }

        private void CheckRecursive(GameObject root, Transform parent, string parentPath, string objectPath, bool isPrefab)
        {
            if (parent == null)
                return;

            // 检查所有子对象
            foreach (Transform child in parent)
            {
                // 如果父对象和子对象同名
                if (parent.name == child.name)
                {
                    string nestedPath = $"{parent.name}/{child.name}";
                    string fullPath = $"{parentPath}/{child.name}";
                    string parentObjPath = parentPath;
                    string childObjPath = $"{parentPath}/{child.name}";

                    // 对于场景对象，保存实际的对象引用；对于预制体，保存路径信息
                    GameObject parentObj = isPrefab ? null : parent.gameObject;
                    GameObject childObj = isPrefab ? null : child.gameObject;

                    // 验证对象引用（仅对场景对象）
                    if (!isPrefab)
                    {
                        if (parentObj == null || childObj == null)
                        {
                            Debug.LogWarning($"检测到同名嵌套但对象引用为null: {fullPath}");
                        }
                    }

                    nestedNameList.Add(new NestedNameInfo(nestedPath, parentObj, childObj, fullPath, objectPath, parentObjPath, childObjPath, isPrefab));
                }

                // 构建当前对象的路径
                string currentPath = $"{parentPath}/{child.name}";

                // 递归检查子对象的子对象
                CheckRecursive(root, child, currentPath, objectPath, isPrefab);
            }
        }

        private void DrawDetectionResults()
        {
            EnsureResultCaches();

            int totalPrefabGroups = prefabGroupsCache.Count;

            if (prefabGroupsCache.Count > 0)
            {
                showPrefabResults = EditorGUILayout.Foldout(showPrefabResults, $"预制体结果（{prefabGroupsCache.Count} 个预制体）", true);
                if (showPrefabResults)
                {
                    int totalPages = Mathf.Max(1, Mathf.CeilToInt(prefabGroupsCache.Count / (float)PrefabGroupsPerPage));
                    DrawPaginationControls(ref prefabResultPageIndex, totalPages, $"第 {prefabResultPageIndex + 1}/{totalPages} 页（每页 {PrefabGroupsPerPage} 个预制体）");

                    int start = prefabResultPageIndex * PrefabGroupsPerPage;
                    int end = Mathf.Min(start + PrefabGroupsPerPage, prefabGroupsCache.Count);
                    for (int i = start; i < end; i++)
                    {
                        DrawPrefabNestedGroup(prefabGroupsCache[i], i + 1);
                    }
                }
            }

            if (sceneEntriesCache.Count > 0)
            {
                showSceneResults = EditorGUILayout.Foldout(showSceneResults, $"场景结果（{sceneEntriesCache.Count} 处）", true);
                if (showSceneResults)
                {
                    int totalPages = Mathf.Max(1, Mathf.CeilToInt(sceneEntriesCache.Count / (float)SceneResultsPerPage));
                    DrawPaginationControls(ref sceneResultPageIndex, totalPages, $"第 {sceneResultPageIndex + 1}/{totalPages} 页（每页 {SceneResultsPerPage} 条）");

                    int start = sceneResultPageIndex * SceneResultsPerPage;
                    int end = Mathf.Min(start + SceneResultsPerPage, sceneEntriesCache.Count);
                    for (int i = start; i < end; i++)
                    {
                        int globalIndex = totalPrefabGroups + i + 1;
                        DrawSceneNestedNameInfo(sceneEntriesCache[i], globalIndex);
                    }
                }
            }
        }

        private void DrawPrefabNestedGroup(PrefabGroupCache group, int displayIndex)
        {
            EditorGUILayout.BeginVertical(boxStyle);

            bool currentState = GetPrefabFoldoutState(group.prefabPath);
            string header = $"{displayIndex}. {group.prefabName}（{group.entries.Count} 处）";
            bool newState = EditorGUILayout.Foldout(currentState, header, true);
            if (newState != currentState)
            {
                SetPrefabFoldoutState(group.prefabPath, newState);
            }

            if (newState)
            {
                EditorGUILayout.Space(3);
                foreach (var info in group.entries)
                {
                    EditorGUILayout.BeginHorizontal();
                    string childDisplayName = GetChildDisplayName(info);
                    EditorGUILayout.LabelField($"子对象: {childDisplayName}", labelStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("定位子对象", GUILayout.Width(110), GUILayout.Height(25)))
                    {
                        LocateObjectInPrefab(info.objectPath, info.childPath);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawSceneNestedNameInfo(NestedNameInfo info, int index)
        {
            EditorGUILayout.BeginVertical(boxStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{index}. {info.path}", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            bool childValid = info.childObj != null;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("子对象:", GUILayout.Width(70));
            EditorGUILayout.ObjectField(childValid ? info.childObj : null, typeof(GameObject), true, GUILayout.ExpandWidth(true));
            EditorGUI.BeginDisabledGroup(!childValid);
            if (GUILayout.Button("定位子对象", GUILayout.Width(80), GUILayout.Height(25)))
            {
                if (info.childObj != null)
                {
                    Selection.activeGameObject = info.childObj;
                    EditorGUIUtility.PingObject(info.childObj);
                }
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!childValid)
            {
                EditorGUILayout.HelpBox("对象引用已失效，可能已被删除", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void LocateObjectInPrefab(string prefabPath, string objectPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return;

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.assetPath != prefabPath)
            {
                stage = PrefabStageUtility.OpenPrefab(prefabPath);
            }

            if (stage == null || stage.prefabContentsRoot == null)
            {
                Debug.LogWarning($"无法打开预制体进行编辑: {prefabPath}");
                return;
            }

            Selection.activeObject = stage.prefabContentsRoot;
            EditorGUIUtility.PingObject(stage.prefabContentsRoot);

            EditorApplication.delayCall += () => TrySelectObjectInPrefabStage(prefabPath, objectPath, 10);
        }

        private void TrySelectObjectInPrefabStage(string prefabPath, string objectPath, int maxRetries)
        {
            if (maxRetries <= 0)
            {
                Debug.LogWarning($"无法在预制体中定位对象: {objectPath}，已达到最大重试次数");
                return;
            }

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.assetPath != prefabPath || stage.prefabContentsRoot == null)
            {
                EditorApplication.delayCall += () => TrySelectObjectInPrefabStage(prefabPath, objectPath, maxRetries - 1);
                return;
            }

            Transform root = stage.prefabContentsRoot.transform;
            Transform target = FindChildByPath(root, objectPath);
            if (target != null)
            {
                Selection.activeGameObject = target.gameObject;
                EditorGUIUtility.PingObject(target.gameObject);
            }
            else
            {
                Selection.activeGameObject = root.gameObject;
                EditorGUIUtility.PingObject(root.gameObject);
                Debug.LogWarning($"无法找到路径: {objectPath}，已选中预制体根对象。请手动在Hierarchy面板中查找: {objectPath}");
            }
        }

        private Transform FindChildByPath(Transform parent, string path)
        {
            if (parent == null || string.IsNullOrEmpty(path))
                return null;

            // 移除根对象名称（如果路径以根对象名称开头）
            string searchPath = path;
            if (path.StartsWith(parent.name + "/"))
            {
                searchPath = path.Substring(parent.name.Length + 1);
            }
            else if (path == parent.name)
            {
                return parent;
            }

            // 按路径分割
            string[] parts = searchPath.Split('/');
            Transform current = parent;

            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part))
                    continue;

                Transform child = current.Find(part);
                if (child == null)
                {
                    // 如果直接Find找不到，遍历所有子对象
                    foreach (Transform t in current)
                    {
                        if (t.name == part)
                        {
                            child = t;
                            break;
                        }
                    }
                }

                if (child == null)
                    return null;

                current = child;
            }

            return current;
        }

        private void InvalidateResultCaches()
        {
            resultsCacheDirty = true;
            prefabIssueCountCache = 0;
        }

        private void EnsureResultCaches()
        {
            if (!resultsCacheDirty)
                return;

            prefabGroupsCache.Clear();
            sceneEntriesCache.Clear();
            prefabFoldoutStates.Clear();

            HashSet<string> prefabPaths = new HashSet<string>();

            var prefabGroups = nestedNameList
                .Where(info => info.isPrefab)
                .GroupBy(info => info.objectPath)
                .OrderBy(group => group.Key ?? string.Empty);

            foreach (var group in prefabGroups)
            {
                PrefabGroupCache cache = new PrefabGroupCache
                {
                    prefabPath = group.Key ?? string.Empty,
                    prefabName = GetPrefabDisplayName(group.Key),
                    entries = group.OrderBy(info => info.childPath).ToList()
                };
                prefabGroupsCache.Add(cache);

                if (!string.IsNullOrEmpty(group.Key))
                {
                    prefabPaths.Add(group.Key);
                }
            }

            sceneEntriesCache.AddRange(
                nestedNameList
                    .Where(info => !info.isPrefab)
                    .OrderBy(info => info.fullPath)
            );

            prefabIssueCountCache = prefabPaths.Count;
            resultsCacheDirty = false;

            prefabResultPageIndex = 0;
            sceneResultPageIndex = 0;
        }

        private string GetPrefabDisplayName(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return "未知预制体";
            return System.IO.Path.GetFileNameWithoutExtension(prefabPath);
        }

        private void DrawPaginationControls(ref int pageIndex, int totalPages, string label)
        {
            if (totalPages <= 1)
                return;

            pageIndex = Mathf.Clamp(pageIndex, 0, totalPages - 1);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(pageIndex <= 0);
            if (GUILayout.Button("上一页", GUILayout.Width(70)))
            {
                pageIndex = Mathf.Max(pageIndex - 1, 0);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.LabelField(label, GUILayout.ExpandWidth(true));

            EditorGUI.BeginDisabledGroup(pageIndex >= totalPages - 1);
            if (GUILayout.Button("下一页", GUILayout.Width(70)))
            {
                pageIndex = Mathf.Min(pageIndex + 1, totalPages - 1);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private bool GetPrefabFoldoutState(string prefabPath)
        {
            string key = string.IsNullOrEmpty(prefabPath) ? "<empty>" : prefabPath;
            if (!prefabFoldoutStates.TryGetValue(key, out bool state))
            {
                state = false;
                prefabFoldoutStates[key] = state;
            }
            return state;
        }

        private void SetPrefabFoldoutState(string prefabPath, bool state)
        {
            string key = string.IsNullOrEmpty(prefabPath) ? "<empty>" : prefabPath;
            prefabFoldoutStates[key] = state;
        }

        private int GetPrefabIssueCount()
        {
            EnsureResultCaches();
            return prefabIssueCountCache;
        }

        private string GetChildDisplayName(NestedNameInfo info)
        {
            if (!string.IsNullOrEmpty(info.childPath))
            {
                int index = info.childPath.LastIndexOf('/');
                if (index >= 0 && index < info.childPath.Length - 1)
                {
                    return info.childPath.Substring(index + 1);
                }
                return info.childPath;
            }

            if (info.childObj != null)
            {
                return info.childObj.name;
            }

            return "未知子对象";
        }
    }
}

