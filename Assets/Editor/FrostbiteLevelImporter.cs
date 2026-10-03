#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace FrostbiteImporter
{
    /// <summary>
    /// Frostbite 1.x Level & Terrain Importer for Unity
    /// ===============================================
    /// Импортирует карту высот террейна (RAW 16-bit 2049x2049), спутниковую текстуру,
    /// расставляет объекты окружения, технику, пехоту и сплайны дорог/рек на сцену.
    ///
    /// Правила статики:
    /// - Вся архитектура (Architecture) и дома (House_Objects) помечаются статичными (isStatic = true).
    /// - Пропсы (Objects, Street_Objects, Vegetation), транспорт и оружие НЕ статичны (isStatic = false).
    ///
    /// Ошибки импорта отдельных объектов безопасно пропускаются.
    /// </summary>
    public class FrostbiteLevelImporter : EditorWindow
    {
        [SerializeField] private string layoutJsonPath = "";
        [SerializeField] private bool importTerrain = true;
        [SerializeField] private bool applyColormapLayer = false;
        [SerializeField] private bool importEnvironmentObjects = true;
        [SerializeField] private bool importVehicles = true;
        [SerializeField] private bool importSoldiers = true;
        [SerializeField] private bool importSplines = true;
        [SerializeField] private bool groupByCategory = true;
        [SerializeField] private bool createMissingPlaceholders = false;

        private Vector2 scrollPos;
        private string statusMessage = "";

        [MenuItem("Frostbite/Import Level Layout", false, 10)]
        public static void ShowWindow()
        {
            var win = GetWindow<FrostbiteLevelImporter>("Frostbite Importer");
            win.minSize = new Vector2(480, 520);
            win.Show();
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(layoutJsonPath))
            {
                // Поиск level_layout.json по умолчанию
                string defaultPath1 = @"C:\Users\Makso\Desktop\bf_unpackers\exported_levels\sp_01_greenacres\level_layout.json";
                string defaultPath2 = Path.Combine(Application.dataPath, "ExportedLevels", "sp_01_greenacres", "level_layout.json");

                if (File.Exists(defaultPath1)) layoutJsonPath = defaultPath1;
                else if (File.Exists(defaultPath2)) layoutJsonPath = defaultPath2;
            }
        }

        private void OnGUI()
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Frostbite 1.x Level Importer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Скрипт расставляет объекты на текущую открытую сцену Unity.\n" +
                "• Архитектура и дома (Architecture, House_Objects) помечаются СТАТИЧНЫМИ.\n" +
                "• Пропсы, растительность и транспорт создаются НЕ СТАТИЧНЫМИ.\n" +
                "• Если модели нет в Assets/Models или возникнет ошибка, объект будет пропущен.",
                MessageType.Info
            );
            EditorGUILayout.Space(6);

            // 1. Выбор JSON файла
            EditorGUILayout.LabelField("Файл расстановки уровня (level_layout.json):", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            layoutJsonPath = EditorGUILayout.TextField(layoutJsonPath);
            if (GUILayout.Button("Обзор...", GUILayout.Width(75)))
            {
                string dir = string.IsNullOrEmpty(layoutJsonPath) ? "" : Path.GetDirectoryName(layoutJsonPath);
                string selected = EditorUtility.OpenFilePanel("Выберите level_layout.json", dir, "json");
                if (!string.IsNullOrEmpty(selected))
                {
                    layoutJsonPath = selected;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Параметры импорта:", EditorStyles.boldLabel);
            importTerrain = EditorGUILayout.ToggleLeft("Импортировать террейн (RAW 16-bit 2049x2049)", importTerrain);
            applyColormapLayer = EditorGUILayout.ToggleLeft("  └─ Наложить слой текстуры Colormap (TerrainLayer)", applyColormapLayer);
            importEnvironmentObjects = EditorGUILayout.ToggleLeft("Импортировать объекты окружения (4000+ зданий, деревьев, пропсов)", importEnvironmentObjects);
            importVehicles = EditorGUILayout.ToggleLeft("Импортировать спавны техники (Vehicles)", importVehicles);
            importSoldiers = EditorGUILayout.ToggleLeft("Импортировать спавны пехоты (Soldiers)", importSoldiers);
            importSplines = EditorGUILayout.ToggleLeft("Импортировать сплайны дорог, рек и водоемов (LineRenderer / Waypoints)", importSplines);
            groupByCategory = EditorGUILayout.ToggleLeft("Группировать объекты по категориям в иерархии", groupByCategory);
            createMissingPlaceholders = EditorGUILayout.ToggleLeft("Создавать пустые маркеры для ненайденных моделей", createMissingPlaceholders);

            EditorGUILayout.Space(14);
            GUI.backgroundColor = new Color(0.2f, 0.8f, 0.3f);
            if (GUILayout.Button("ИМПОРТИРОВАТЬ НА СЦЕНУ", GUILayout.Height(38)))
            {
                if (string.IsNullOrEmpty(layoutJsonPath) || !File.Exists(layoutJsonPath))
                {
                    EditorUtility.DisplayDialog("Ошибка", "Файл level_layout.json не найден!\nУкажите корректный путь.", "ОК");
                }
                else
                {
                    ExecuteImport();
                }
            }
            GUI.backgroundColor = Color.white;

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox(statusMessage, MessageType.None);
            }

            EditorGUILayout.EndScrollView();
        }

        private void ExecuteImport()
        {
            string jsonText = File.ReadAllText(layoutJsonPath);
            var root = JSONNode.Parse(jsonText);
            if (root == null)
            {
                EditorUtility.DisplayDialog("Ошибка", "Не удалось разобрать JSON файл.", "ОК");
                return;
            }

            string levelName = root["level_name"].Value ?? "Frostbite_Level";
            string layoutDir = Path.GetDirectoryName(layoutJsonPath);

            GameObject rootGO = new GameObject($"[Level] {levelName}");
            Undo.RegisterCreatedObjectUndo(rootGO, "Import Frostbite Level");

            int importedObjects = 0;
            int skippedObjects = 0;
            int importedVehicles = 0;
            int importedSoldiers = 0;
            int importedSplines = 0;

            try
            {
                // 1. ИМПОРТ ТЕРРЕЙНА
                if (importTerrain)
                {
                    EditorUtility.DisplayProgressBar("Frostbite Importer", "Импорт террейна...", 0.05f);
                    ImportTerrainData(root["terrain"], root["terrain_entity_offset"], layoutDir, rootGO);
                }

                // 2. ИМПОРТ ОБЪЕКТОВ ОКРУЖЕНИЯ
                if (importEnvironmentObjects && root["objects"] is JSONArray objectsArr)
                {
                    Dictionary<string, Transform> categoryParents = new Dictionary<string, Transform>();
                    Transform objectsRoot = new GameObject("Environment_Objects").transform;
                    objectsRoot.SetParent(rootGO.transform, false);

                    int total = objectsArr.Count;
                    for (int i = 0; i < total; i++)
                    {
                        if (i % 50 == 0)
                        {
                            float p = 0.1f + 0.65f * ((float)i / total);
                            EditorUtility.DisplayProgressBar("Frostbite Importer", $"Расстановка объектов ({i}/{total})...", p);
                        }

                        var objNode = objectsArr[i];
                        try
                        {
                            string category = objNode["category"].Value ?? "Props";
                            string modelName = objNode["model_name"].Value ?? "Object";
                            string modelPath = objNode["unity_model_path"].Value;

                            // Определяем категорию для группировки
                            Transform parentTransform = objectsRoot;
                            if (groupByCategory)
                            {
                                if (!categoryParents.TryGetValue(category, out parentTransform))
                                {
                                    GameObject catGO = new GameObject(category);
                                    catGO.transform.SetParent(objectsRoot, false);
                                    categoryParents[category] = catGO.transform;
                                    parentTransform = catGO.transform;
                                }
                            }

                            // Правило статики: только Architecture и House_Objects статичны!
                            bool isStatic = category.Equals("Architecture", StringComparison.OrdinalIgnoreCase) ||
                                            category.Equals("House_Objects", StringComparison.OrdinalIgnoreCase);

                            GameObject inst = null;
                            if (!string.IsNullOrEmpty(modelPath))
                            {
                                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                                if (prefab != null)
                                {
                                    inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                                }
                            }

                            if (inst == null)
                            {
                                if (createMissingPlaceholders)
                                {
                                    inst = new GameObject(modelName + " [Missing]");
                                }
                                else
                                {
                                    skippedObjects++;
                                    continue; // Пропускаем объект безопасно
                                }
                            }

                            inst.name = modelName;
                            inst.transform.SetParent(parentTransform, false);

                            // Применение трансформации
                            ApplyUnityTransform(inst.transform, objNode["unity"]);

                            // Применение статики (включая все дочерние меши)
                            SetStaticRecursively(inst, isStatic);

                            importedObjects++;
                        }
                        catch (Exception ex)
                        {
                            // Ошибки импорта безопасно игнорируются
                            skippedObjects++;
                            Debug.LogWarning($"[FrostbiteImporter] Ошибка импорта объекта {objNode["model_name"].Value}: {ex.Message}");
                        }
                    }
                }

                // 3. ИМПОРТ СПАВНОВ ТЕХНИКИ
                if (importVehicles && root["vehicles"] is JSONArray vehArr)
                {
                    EditorUtility.DisplayProgressBar("Frostbite Importer", "Импорт спавнов техники...", 0.80f);
                    Transform vehRoot = new GameObject("Vehicles").transform;
                    vehRoot.SetParent(rootGO.transform, false);

                    for (int i = 0; i < vehArr.Count; i++)
                    {
                        var vNode = vehArr[i];
                        try
                        {
                            string vName = vNode["name"].Value ?? "Vehicle";
                            string modelPath = vNode["unity_model_path"].Value;

                            GameObject inst = null;
                            if (!string.IsNullOrEmpty(modelPath))
                            {
                                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                                if (prefab != null)
                                {
                                    inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                                }
                            }

                            if (inst == null)
                            {
                                inst = new GameObject(vName);
                            }

                            inst.name = vName;
                            inst.transform.SetParent(vehRoot, false);
                            ApplyUnityTransform(inst.transform, vNode["unity"]);

                            // Транспорт НЕ должен быть статичным!
                            SetStaticRecursively(inst, false);
                            importedVehicles++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[FrostbiteImporter] Пропуск техники {vNode["name"].Value}: {ex.Message}");
                        }
                    }
                }

                // 4. ИМПОРТ СПАВНОВ ПЕХОТЫ
                if (importSoldiers && root["soldiers"] is JSONArray solArr)
                {
                    EditorUtility.DisplayProgressBar("Frostbite Importer", "Импорт спавнов пехоты...", 0.88f);
                    Transform solRoot = new GameObject("Soldiers").transform;
                    solRoot.SetParent(rootGO.transform, false);

                    for (int i = 0; i < solArr.Count; i++)
                    {
                        var sNode = solArr[i];
                        try
                        {
                            string sName = sNode["name"].Value ?? "Soldier";
                            string modelPath = sNode["unity_model_path"].Value;

                            GameObject inst = null;
                            if (!string.IsNullOrEmpty(modelPath))
                            {
                                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                                if (prefab != null)
                                {
                                    inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                                }
                            }

                            if (inst == null)
                            {
                                inst = new GameObject(sName);
                            }

                            inst.name = sName;
                            inst.transform.SetParent(solRoot, false);
                            ApplyUnityTransform(inst.transform, sNode["unity"]);

                            // Пехота НЕ статична
                            SetStaticRecursively(inst, false);
                            importedSoldiers++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[FrostbiteImporter] Пропуск спавна солдата {sNode["name"].Value}: {ex.Message}");
                        }
                    }
                }

                // 5. ИМПОРТ СПЛАЙНОВ (ДОРОГИ, РЕКИ, ОЗЕРА)
                if (importSplines && root["splines"] is JSONArray splinesArr)
                {
                    EditorUtility.DisplayProgressBar("Frostbite Importer", "Импорт сплайнов дорог и рек...", 0.94f);
                    Transform splinesRoot = new GameObject("Splines").transform;
                    splinesRoot.SetParent(rootGO.transform, false);

                    Transform roadsGroup = new GameObject("Roads").transform;
                    roadsGroup.SetParent(splinesRoot, false);
                    Transform riversGroup = new GameObject("Rivers").transform;
                    riversGroup.SetParent(splinesRoot, false);
                    Transform lakesGroup = new GameObject("Lakes").transform;
                    lakesGroup.SetParent(splinesRoot, false);

                    for (int i = 0; i < splinesArr.Count; i++)
                    {
                        var spNode = splinesArr[i];
                        try
                        {
                            string sName = spNode["name"].Value ?? $"Spline_{i}";
                            string sType = spNode["type"].Value ?? "Road";
                            float width = spNode["width"].AsFloat;
                            if (width < 0.1f) width = 6.0f;

                            var ptsNode = spNode["unity"]["points"] as JSONArray;
                            if (ptsNode == null || ptsNode.Count == 0) continue;

                            Vector3[] points = new Vector3[ptsNode.Count];
                            for (int p = 0; p < ptsNode.Count; p++)
                            {
                                var ptArr = ptsNode[p] as JSONArray;
                                points[p] = new Vector3(ptArr[0].AsFloat, ptArr[1].AsFloat, ptArr[2].AsFloat);
                            }

                            GameObject splineGO = new GameObject(sName);
                            Transform targetParent = roadsGroup;
                            Color splineColor = new Color(0.4f, 0.4f, 0.4f, 0.9f); // Дорога

                            if (sType.Equals("River", StringComparison.OrdinalIgnoreCase))
                            {
                                targetParent = riversGroup;
                                splineColor = new Color(0.1f, 0.6f, 0.9f, 0.8f);
                            }
                            else if (sType.Equals("Lake", StringComparison.OrdinalIgnoreCase))
                            {
                                targetParent = lakesGroup;
                                splineColor = new Color(0.05f, 0.3f, 0.8f, 0.8f);
                            }

                            splineGO.transform.SetParent(targetParent, false);

                            // Добавляем LineRenderer для визуализации пути
                            LineRenderer lr = splineGO.AddComponent<LineRenderer>();
                            lr.positionCount = points.Length;
                            lr.SetPositions(points);
                            lr.startWidth = width;
                            lr.endWidth = width;
                            lr.useWorldSpace = true;

                            Material lineMat = new Material(Shader.Find("Sprites/Default"));
                            lineMat.color = splineColor;
                            lr.material = lineMat;

                            // Создаем дочерние точки-пустышки для удобного редактирования
                            for (int p = 0; p < points.Length; p++)
                            {
                                GameObject ptGO = new GameObject($"Point_{p:D2}");
                                ptGO.transform.SetParent(splineGO.transform, false);
                                ptGO.transform.position = points[p];
                            }

                            importedSplines++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[FrostbiteImporter] Пропуск сплайна {spNode["name"].Value}: {ex.Message}");
                        }
                    }
                }

                statusMessage = $"Импорт успешно завершен!\n" +
                                $"• Расставлено объектов: {importedObjects} (пропущено: {skippedObjects})\n" +
                                $"• Техника: {importedVehicles}\n" +
                                $"• Пехота: {importedSoldiers}\n" +
                                $"• Сплайнов дорог/рек: {importedSplines}";

                EditorUtility.DisplayDialog("Импорт завершен", statusMessage, "ОК");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ImportTerrainData(JSONNode terrainNode, JSONNode offsetNode, string layoutDir, GameObject rootGO)
        {
            if (terrainNode == null) return;

            try
            {
                string rawPath = Path.Combine(layoutDir, "Terrain", "terrain_heightmap_2049.raw");
                if (!File.Exists(rawPath))
                {
                    Debug.LogWarning($"[FrostbiteImporter] RAW карта высот не найдена: {rawPath}");
                    return;
                }

                float sizeX = terrainNode["size"]["width_x"].AsFloat;
                float sizeY = terrainNode["size"]["height_y"].AsFloat;
                float sizeZ = terrainNode["size"]["length_z"].AsFloat;
                if (sizeX <= 0) sizeX = 2048f;
                if (sizeY <= 0) sizeY = 500f;
                if (sizeZ <= 0) sizeZ = 2048f;

                float posY = 0f; // Абсолютная базовая высота террейна в Unity всегда 0.0

                int res = 2049;
                byte[] rawBytes = File.ReadAllBytes(rawPath);
                if (rawBytes.Length < res * res * 2)
                {
                    Debug.LogWarning($"[FrostbiteImporter] Размер RAW файла меньше ожидаемого: {rawBytes.Length}");
                    return;
                }

                float[,] heights = new float[res, res];
                int byteIdx = 0;
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        ushort val = (ushort)(rawBytes[byteIdx] | (rawBytes[byteIdx + 1] << 8));
                        heights[z, x] = val / 65535.0f;
                        byteIdx += 2;
                    }
                }

                TerrainData terrainData = new TerrainData();
                terrainData.name = "TerrainData_" + (rootGO.name.Replace("[Level] ", ""));
                terrainData.heightmapResolution = res;
                terrainData.size = new Vector3(sizeX, sizeY, sizeZ);
                terrainData.SetHeights(0, 0, heights);

                // Подключение текстурного слоя Colormap только если включен чекбокс
                if (applyColormapLayer)
                {
                    string colormapPath = Path.Combine(layoutDir, "Terrain", "terrain_colormap.png");
                    if (File.Exists(colormapPath))
                    {
                        byte[] pngBytes = File.ReadAllBytes(colormapPath);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                        tex.LoadImage(pngBytes);
                        tex.name = "Terrain_Colormap";

                        TerrainLayer layer = new TerrainLayer();
                        layer.diffuseTexture = tex;
                        layer.tileSize = new Vector2(sizeX, sizeZ);
                        layer.tileOffset = Vector2.zero;
                        terrainData.terrainLayers = new TerrainLayer[] { layer };
                    }
                }
                else
                {
                    terrainData.terrainLayers = new TerrainLayer[0];
                }

                GameObject terrainGO = Terrain.CreateTerrainGameObject(terrainData);
                terrainGO.name = "Terrain";
                terrainGO.transform.SetParent(rootGO.transform, false);

                // Выравнивание центра: в Frostbite координаты мира от -1024 до +1024, базовая высота Y = 0.0
                float posX = -sizeX * 0.5f;
                float posZ = -sizeZ * 0.5f;
                terrainGO.transform.position = new Vector3(posX, 0.0f, posZ);

                // Террейн ВСЕГДА статичен
                SetStaticRecursively(terrainGO, true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FrostbiteImporter] Ошибка импорта террейна: {ex.Message}");
            }
        }

        private void ApplyUnityTransform(Transform t, JSONNode unityNode)
        {
            if (unityNode == null) return;

            var pos = unityNode["position"] as JSONArray;
            var rot = unityNode["rotation_quaternion"] as JSONArray;
            var scale = unityNode["scale"] as JSONArray;

            if (pos != null && pos.Count == 3)
            {
                t.position = new Vector3(pos[0].AsFloat, pos[1].AsFloat, pos[2].AsFloat);
            }

            if (rot != null && rot.Count == 4)
            {
                t.rotation = new Quaternion(rot[0].AsFloat, rot[1].AsFloat, rot[2].AsFloat, rot[3].AsFloat);
            }

            if (scale != null && scale.Count == 3)
            {
                t.localScale = new Vector3(scale[0].AsFloat, scale[1].AsFloat, scale[2].AsFloat);
            }
        }

        private void SetStaticRecursively(GameObject go, bool isStatic)
        {
            go.isStatic = isStatic;
            foreach (Transform child in go.transform)
            {
                SetStaticRecursively(child.gameObject, isStatic);
            }
        }
    }

    // ==============================================================================
    // Self-Contained Lightweight JSON Parser (No External Dependencies)
    // ==============================================================================
    public enum JSONNodeType { Array, Object, String, Number, Boolean, Null }

    public class JSONNode
    {
        public virtual JSONNode this[int index] { get { return null; } set { } }
        public virtual JSONNode this[string key] { get { return null; } set { } }
        public virtual string Value { get { return ""; } set { } }
        public virtual int Count { get { return 0; } }
        public virtual float AsFloat { get { return float.TryParse(Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f; } }
        public virtual int AsInt { get { return int.TryParse(Value, out int v) ? v : 0; } }
        public virtual bool AsBool { get { return bool.TryParse(Value, out bool v) && v; } }

        public static JSONNode Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int idx = 0;
            return ParseValue(json, ref idx);
        }

        private static JSONNode ParseValue(string json, ref int idx)
        {
            SkipWhitespace(json, ref idx);
            if (idx >= json.Length) return null;

            char c = json[idx];
            if (c == '{') return ParseObject(json, ref idx);
            if (c == '[') return ParseArray(json, ref idx);
            if (c == '"') return new JSONData(ParseString(json, ref idx));
            if (c == 't' || c == 'f') return new JSONData(ParseBool(json, ref idx));
            if (c == 'n') { idx += 4; return new JSONData(null); }
            return new JSONData(ParseNumber(json, ref idx));
        }

        private static JSONObject ParseObject(string json, ref int idx)
        {
            JSONObject obj = new JSONObject();
            idx++; // skip '{'
            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length) break;
                if (json[idx] == '}') { idx++; return obj; }

                string key = ParseString(json, ref idx);
                SkipWhitespace(json, ref idx);
                if (idx < json.Length && json[idx] == ':') idx++; // skip ':'

                JSONNode val = ParseValue(json, ref idx);
                obj[key] = val;

                SkipWhitespace(json, ref idx);
                if (idx < json.Length && json[idx] == ',') idx++;
            }
            return obj;
        }

        private static JSONArray ParseArray(string json, ref int idx)
        {
            JSONArray arr = new JSONArray();
            idx++; // skip '['
            while (idx < json.Length)
            {
                SkipWhitespace(json, ref idx);
                if (idx >= json.Length) break;
                if (json[idx] == ']') { idx++; return arr; }

                JSONNode val = ParseValue(json, ref idx);
                arr.Add(val);

                SkipWhitespace(json, ref idx);
                if (idx < json.Length && json[idx] == ',') idx++;
            }
            return arr;
        }

        private static string ParseString(string json, ref int idx)
        {
            idx++; // skip opening quote
            int start = idx;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            while (idx < json.Length)
            {
                char c = json[idx++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && idx < json.Length)
                {
                    char esc = json[idx++];
                    if (esc == '"') sb.Append('"');
                    else if (esc == '\\') sb.Append('\\');
                    else if (esc == '/') sb.Append('/');
                    else if (esc == 'n') sb.Append('\n');
                    else if (esc == 'r') sb.Append('\r');
                    else if (esc == 't') sb.Append('\t');
                    else sb.Append(esc);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string ParseNumber(string json, ref int idx)
        {
            int start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-' || json[idx] == '+' || json[idx] == '.' || json[idx] == 'e' || json[idx] == 'E'))
            {
                idx++;
            }
            return json.Substring(start, idx - start);
        }

        private static string ParseBool(string json, ref int idx)
        {
            if (json[idx] == 't') { idx += 4; return "true"; }
            else { idx += 5; return "false"; }
        }

        private static void SkipWhitespace(string json, ref int idx)
        {
            while (idx < json.Length && char.IsWhiteSpace(json[idx])) idx++;
        }
    }

    public class JSONObject : JSONNode
    {
        private Dictionary<string, JSONNode> dict = new Dictionary<string, JSONNode>(StringComparer.OrdinalIgnoreCase);
        public override JSONNode this[string key]
        {
            get => dict.TryGetValue(key, out var node) ? node : null;
            set => dict[key] = value;
        }
        public override int Count => dict.Count;
    }

    public class JSONArray : JSONNode, IEnumerable<JSONNode>
    {
        private List<JSONNode> list = new List<JSONNode>();
        public override JSONNode this[int index]
        {
            get => (index >= 0 && index < list.Count) ? list[index] : null;
            set => list[index] = value;
        }
        public override int Count => list.Count;
        public void Add(JSONNode item) => list.Add(item);
        public IEnumerator<JSONNode> GetEnumerator() => list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => list.GetEnumerator();
    }

    public class JSONData : JSONNode
    {
        private string val;
        public JSONData(string value) => val = value;
        public override string Value { get => val; set => val = value; }
    }
}
#endif
