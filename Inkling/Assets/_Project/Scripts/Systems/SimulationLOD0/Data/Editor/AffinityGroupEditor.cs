using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;

namespace Magi.Inkling.Systems.SimulationLOD0.Editor
{
    [CustomEditor(typeof(AffinityGroup))]
    public class AffinityGroupEditor : UnityEditor.Editor
    {
        private SerializedProperty groupName;
        private SerializedProperty inks;
        private SerializedProperty productMatrix;
        private SerializedProperty productCol4;
        private SerializedProperty productCol5;
        private SerializedProperty reactionImpulseMatrix;
        private SerializedProperty reactionImpulseCol4;
        private SerializedProperty reactionImpulseCol5;
        private SerializedProperty reactionRateMultiplier;
        private SerializedProperty selfWeight;
        private SerializedProperty cardinalWeight;
        private SerializedProperty diagonalWeight;
        private SerializedProperty thermalTransitions;
        private SerializedProperty thermalSources;

        private bool showProductMatrix = true;
        private bool showReactionImpulseMatrix = true;
        private bool showThermal = true;

        private ReorderableList transitionList;
        private ReorderableList sourceList;

        private void OnEnable()
        {
            groupName = serializedObject.FindProperty("groupName");
            inks = serializedObject.FindProperty("inks");
            productMatrix = serializedObject.FindProperty("productMatrix");
            productCol4 = serializedObject.FindProperty("productCol4");
            productCol5 = serializedObject.FindProperty("productCol5");
            reactionImpulseMatrix = serializedObject.FindProperty("reactionImpulseMatrix");
            reactionImpulseCol4 = serializedObject.FindProperty("reactionImpulseCol4");
            reactionImpulseCol5 = serializedObject.FindProperty("reactionImpulseCol5");
            reactionRateMultiplier = serializedObject.FindProperty("reactionRateMultiplier");
            selfWeight = serializedObject.FindProperty("selfWeight");
            cardinalWeight = serializedObject.FindProperty("cardinalWeight");
            diagonalWeight = serializedObject.FindProperty("diagonalWeight");
            thermalTransitions = serializedObject.FindProperty("thermalTransitions");
            thermalSources = serializedObject.FindProperty("thermalSources");

            BuildThermalLists();
        }

        // ── Thermal authoring surface (CP7d slice 1b) ───────────────────────────────────────
        // Ordered lists, NOT raw arrays: the execution order (all Cold in authored order, then all
        // Hot in authored order) is load-bearing, so the author must be able to see and drag it.

        private void BuildThermalLists()
        {
            transitionList = new ReorderableList(serializedObject, thermalTransitions,
                draggable: true, displayHeader: true, displayAddButton: true, displayRemoveButton: true);
            transitionList.drawHeaderCallback = rect =>
                EditorGUI.LabelField(rect, "Transitions — runs: all Cold (in order), then all Hot (in order)");
            transitionList.elementHeightCallback = _ => (EditorGUIUtility.singleLineHeight + 2f) * 2f + 6f;
            transitionList.drawElementCallback = (rect, index, _, __) => DrawTransitionElement(rect, index);

            sourceList = new ReorderableList(serializedObject, thermalSources,
                draggable: true, displayHeader: true, displayAddButton: true, displayRemoveButton: true);
            sourceList.drawHeaderCallback = rect =>
                EditorGUI.LabelField(rect, "Heat Sources — local emission; fuel burns only for heat actually added");
            sourceList.elementHeightCallback = _ => EditorGUIUtility.singleLineHeight + 6f;
            sourceList.drawElementCallback = (rect, index, _, __) => DrawSourceElement(rect, index);
        }

        private static readonly int[] SlotValues = { 0, 1, 2, 3 };

        private void DrawTransitionElement(Rect rect, int index)
        {
            SerializedProperty el = thermalTransitions.GetArrayElementAtIndex(index);
            SerializedProperty from = el.FindPropertyRelative("fromSlot");
            SerializedProperty to = el.FindPropertyRelative("toSlot");
            SerializedProperty regime = el.FindPropertyRelative("regime");
            SerializedProperty threshold = el.FindPropertyRelative("threshold");
            SerializedProperty rate = el.FindPropertyRelative("rate");
            SerializedProperty heatCost = el.FindPropertyRelative("heatCost");
            SerializedProperty heatRelease = el.FindPropertyRelative("heatRelease");

            string[] names = GetInkNames();
            float line = EditorGUIUtility.singleLineHeight;
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 48f;

            // Row 1: From → To | Regime
            float y = rect.y + 3f;
            float third = (rect.width - 26f) / 3f;
            from.intValue = EditorGUI.IntPopup(new Rect(rect.x, y, third, line), from.intValue, names, SlotValues);
            EditorGUI.LabelField(new Rect(rect.x + third + 4f, y, 18f, line), "→");
            to.intValue = EditorGUI.IntPopup(new Rect(rect.x + third + 22f, y, third, line), to.intValue, names, SlotValues);
            EditorGUI.PropertyField(new Rect(rect.x + 2f * third + 26f, y, third, line), regime, GUIContent.none);

            // Row 2: Threshold | Rate | (Hot ? heat Cost : heat Release)
            y += line + 2f;
            float col = (rect.width - 8f) / 3f;
            bool hot = regime.enumValueIndex == (int)ThermalRegime.Hot;
            EditorGUI.PropertyField(new Rect(rect.x, y, col - 4f, line), threshold, new GUIContent("Thr"));
            EditorGUI.PropertyField(new Rect(rect.x + col, y, col - 4f, line), rate, new GUIContent("Rate"));
            EditorGUI.PropertyField(new Rect(rect.x + 2f * col, y, col, line),
                hot ? heatCost : heatRelease,
                new GUIContent(hot ? "Cost" : "Release"));

            EditorGUIUtility.labelWidth = prevLabel;
        }

        private void DrawSourceElement(Rect rect, int index)
        {
            SerializedProperty el = thermalSources.GetArrayElementAtIndex(index);
            SerializedProperty slot = el.FindPropertyRelative("slot");
            SerializedProperty emit = el.FindPropertyRelative("heatEmissionRate");
            SerializedProperty fuelCost = el.FindPropertyRelative("fuelCost");

            string[] names = GetInkNames();
            float line = EditorGUIUtility.singleLineHeight;
            float y = rect.y + 3f;
            float col = (rect.width - 8f) / 3f;
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 48f;

            slot.intValue = EditorGUI.IntPopup(new Rect(rect.x, y, col - 4f, line), slot.intValue, names, SlotValues);
            EditorGUI.PropertyField(new Rect(rect.x + col, y, col - 4f, line), emit, new GUIContent("Emit"));
            EditorGUI.PropertyField(new Rect(rect.x + 2f * col, y, col, line), fuelCost, new GUIContent("Fuel"));

            EditorGUIUtility.labelWidth = prevLabel;
        }

        /// <summary>
        /// Read-only matrix-styled preview of the AUTHORED transitions: one 4x4 grid per regime, rows =
        /// FROM ink, columns = TO ink, cell = the transition's execution position (1-based) and rate.
        /// This gives the familiar matrix at-a-glance view WITHOUT pretending the data is a matrix —
        /// the ordered list below remains authoritative, which is why cells show their order index.
        /// </summary>
        private void DrawThermalMatrixPreview()
        {
            if (thermalTransitions == null || thermalTransitions.arraySize == 0)
                return;

            string[] names = GetInkNames();
            DrawRegimeGrid("Cold (heat < threshold)", ThermalRegime.Cold, names);
            DrawRegimeGrid("Hot (excess heat above threshold)", ThermalRegime.Hot, names);
        }

        private void DrawRegimeGrid(string title, ThermalRegime regime, string[] names)
        {
            // Collect authored transitions of this regime, preserving authored order.
            var cells = new Dictionary<(int, int), string>();
            int order = 0;
            for (int i = 0; i < thermalTransitions.arraySize; i++)
            {
                SerializedProperty el = thermalTransitions.GetArrayElementAtIndex(i);
                if (el.FindPropertyRelative("regime").enumValueIndex != (int)regime) continue;

                order++;
                int from = Mathf.Clamp(el.FindPropertyRelative("fromSlot").intValue, 0, 3);
                int to = Mathf.Clamp(el.FindPropertyRelative("toSlot").intValue, 0, 3);
                float rate = el.FindPropertyRelative("rate").floatValue;
                cells[(from, to)] = $"{order}: {rate:0.##}";
            }

            if (cells.Count == 0) return;

            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;

            const float labelWidth = 70f;
            const float cellWidth = 60f;

            // Header: TO inks.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(labelWidth + 15);
            for (int col = 0; col < 4; col++)
                GUILayout.Label("→" + names[col], EditorStyles.miniLabel, GUILayout.Width(cellWidth));
            EditorGUILayout.EndHorizontal();

            // Rows: FROM inks. Cell text = "<execution order>: <rate>".
            for (int row = 0; row < 4; row++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(names[row], GUILayout.Width(labelWidth));
                for (int col = 0; col < 4; col++)
                {
                    cells.TryGetValue((row, col), out string text);
                    GUILayout.Label(string.IsNullOrEmpty(text) ? "·" : text,
                        EditorStyles.miniLabel, GUILayout.Width(cellWidth));
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Live validation: bake THIS group and surface the result before the author edits a shipped
        /// asset. Note this validates the group in isolation — cross-group collisions (the same
        /// resolved ink used as a source in two groups) are only detectable when all active groups
        /// are baked together at runtime, so that caveat is stated in the UI.
        /// </summary>
        private void DrawThermalValidation()
        {
            var group = (AffinityGroup)target;
            ThermalRuleSet rules = ThermalRuleBaker.Bake(new List<AffinityGroup> { group }, ThermalDefaults.Cp7Defaults);

            if (!rules.IsValid)
            {
                EditorGUILayout.HelpBox(
                    "Thermal rules INVALID — the entire set is inert at runtime (no thermal phase changes will run):\n\n" +
                    rules.Error,
                    MessageType.Error);
                return;
            }

            foreach (string w in rules.Warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            if (rules.UsedDefaultTransitions && rules.UsedDefaultSources)
            {
                EditorGUILayout.HelpBox(
                    "No thermal data authored on this group — the built-in CP7 defaults are used " +
                    "(condense, freeze, melt, boil, plus the fire heat source).",
                    MessageType.Info);
                return;
            }

            int cold = 0, hot = 0;
            foreach (var t in rules.Transitions)
            {
                if (t.regime == ThermalRegime.Cold) cold++;
                else hot++;
            }

            string summary = $"Thermal rules valid: {cold} cold + {hot} hot transition(s), {rules.Sources.Count} source(s).";
            if (rules.UsedDefaultTransitions) summary += " Transitions fall back to CP7 defaults.";
            if (rules.UsedDefaultSources) summary += " Sources fall back to the CP7 default.";
            summary += "\n\nValidated for THIS group only — cross-group collisions are checked when all active groups are baked together.";
            EditorGUILayout.HelpBox(summary, MessageType.Info);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Basic info
            EditorGUILayout.PropertyField(groupName);
            EditorGUILayout.PropertyField(inks, true);

            EditorGUILayout.Space(10);

            // Get ink names for labels + the 6 ink-pair column labels (shared by both matrices).
            string[] inkNames = GetInkNames();
            string[] productLabels = new string[]
            {
                $"{inkNames[0]}×{inkNames[1]}",
                $"{inkNames[0]}×{inkNames[2]}",
                $"{inkNames[0]}×{inkNames[3]}",
                $"{inkNames[1]}×{inkNames[2]}",
                $"{inkNames[1]}×{inkNames[3]}",
                $"{inkNames[2]}×{inkNames[3]}"
            };

            // Product Reaction Matrix
            showProductMatrix = EditorGUILayout.Foldout(showProductMatrix, "Product Reaction Matrix (A + B → C)", true, EditorStyles.foldoutHeader);
            if (showProductMatrix)
            {
                EditorGUILayout.HelpBox("For reactions requiring TWO inks present.\nColumn = product of two inks, Row = ink affected.", MessageType.Info);

                // Draw 4x6 product matrix (4x4 main + 2 extra columns)
                DrawProductMatrix(productMatrix, productCol4, productCol5, inkNames, productLabels);
            }

            EditorGUILayout.Space(10);

            // Reaction Impulse Matrix (motion) — same 6-column layout as the product matrix.
            showReactionImpulseMatrix = EditorGUILayout.Foldout(showReactionImpulseMatrix, "Reaction Impulse Matrix (motion, A + B → C)", true, EditorStyles.foldoutHeader);
            if (showReactionImpulseMatrix)
            {
                EditorGUILayout.HelpBox(
                    "Drives fluid MOTION from reactions, independent of the product matrix above " +
                    "(concentration conversion speed). Same layout: Column = ink pair A×B, Row = signed intensity.\n" +
                    "Direction for pair A×B is grad(B) - grad(A) (front normal A→B/fuel). Put a signed " +
                    "coefficient in row C for 'A + B → C'; negative flips the direction.\n" +
                    "OrganicGroup: the Fire row under Fire×PlantSeeded and Fire×PlantGrown pushes fire into plant.",
                    MessageType.Info);

                // Same 6 ink-pair columns as the product matrix (labels hoisted above).
                DrawProductMatrix(reactionImpulseMatrix, reactionImpulseCol4, reactionImpulseCol5, inkNames, productLabels);
            }

            EditorGUILayout.Space(10);

            // Thermal Transitions (CP7d) — a deliberately DIFFERENT surface from the two matrices above.
            showThermal = EditorGUILayout.Foldout(showThermal, "Thermal Transitions (local, heat-driven, ORDERED)", true, EditorStyles.foldoutHeader);
            if (showThermal)
            {
                EditorGUILayout.HelpBox(
                    "LOCAL, heat-gated directed transitions (A → B within the SAME cell), driven by that " +
                    "cell's own heat. These are NOT pairwise adjacency products — do not confuse them with " +
                    "the Product/Impulse matrices above. There is no neighbour sampling, so a transition " +
                    "can never mint mass: every conversion is a paired from-- / to++.\n\n" +
                    "ORDER IS LOAD-BEARING. Execution is: heat emission → all COLD transitions in authored " +
                    "order → all HOT transitions in authored order → clamp. Drag rows to change the order.\n\n" +
                    "Cold fires when heat < threshold (rate-limited, may RELEASE heat).\n" +
                    "Hot fires on excess heat above threshold, additionally capped by excess / heat Cost.\n\n" +
                    "Leave BOTH lists empty to use the built-in CP7 defaults (condense, freeze, melt, boil, " +
                    "plus the fire heat source). Replacement is per-category: authoring transitions replaces " +
                    "the default transitions, authoring sources replaces the default source — independently.",
                    MessageType.Info);

                EditorGUILayout.Space(4);
                DrawThermalMatrixPreview();

                EditorGUILayout.Space(4);
                transitionList?.DoLayoutList();
                sourceList?.DoLayoutList();

                EditorGUILayout.Space(4);
                DrawThermalValidation();
            }

            EditorGUILayout.Space(10);

            // Rate Settings
            EditorGUILayout.LabelField("Rate Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(reactionRateMultiplier);

            EditorGUILayout.Space(10);

            // Adjacency Weights
            EditorGUILayout.LabelField("Adjacency Weights", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(selfWeight);
            EditorGUILayout.PropertyField(cardinalWeight);
            EditorGUILayout.PropertyField(diagonalWeight);

            serializedObject.ApplyModifiedProperties();
        }

        private string[] GetInkNames()
        {
            AffinityGroup group = (AffinityGroup)target;
            string[] names = new string[4];
            for (int i = 0; i < 4; i++)
            {
                if (group.inks != null && i < group.inks.Length && group.inks[i] != null)
                {
                    names[i] = group.inks[i].inkType.ToString();
                    // Truncate long names
                    if (names[i].Length > 8)
                        names[i] = names[i].Substring(0, 7) + "…";
                }
                else
                {
                    names[i] = $"Slot {i}";
                }
            }
            return names;
        }

        private void DrawProductMatrix(SerializedProperty mainMatrix, SerializedProperty col4, SerializedProperty col5,
            string[] rowLabels, string[] productLabels)
        {
            EditorGUI.indentLevel++;

            float labelWidth = 70f;
            float cellWidth = 60f;

            // Header row with product column labels
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(labelWidth + 15);
            for (int col = 0; col < 6; col++)
            {
                GUILayout.Label(productLabels[col], EditorStyles.miniLabel, GUILayout.Width(cellWidth));
            }
            EditorGUILayout.EndHorizontal();

            // Matrix rows
            for (int row = 0; row < 4; row++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(rowLabels[row], GUILayout.Width(labelWidth));

                // Columns 0-3 from main matrix
                for (int col = 0; col < 4; col++)
                {
                    string propPath = GetMatrix4x4ElementPath(col, row);
                    SerializedProperty element = mainMatrix.FindPropertyRelative(propPath);
                    if (element != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        float newValue = EditorGUILayout.FloatField(element.floatValue, GUILayout.Width(cellWidth));
                        if (EditorGUI.EndChangeCheck())
                        {
                            element.floatValue = newValue;
                        }
                    }
                }

                // Column 4 from productCol4
                {
                    string propPath = GetVector4ElementPath(row);
                    SerializedProperty element = col4.FindPropertyRelative(propPath);
                    if (element != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        float newValue = EditorGUILayout.FloatField(element.floatValue, GUILayout.Width(cellWidth));
                        if (EditorGUI.EndChangeCheck())
                        {
                            element.floatValue = newValue;
                        }
                    }
                }

                // Column 5 from productCol5
                {
                    string propPath = GetVector4ElementPath(row);
                    SerializedProperty element = col5.FindPropertyRelative(propPath);
                    if (element != null)
                    {
                        EditorGUI.BeginChangeCheck();
                        float newValue = EditorGUILayout.FloatField(element.floatValue, GUILayout.Width(cellWidth));
                        if (EditorGUI.EndChangeCheck())
                        {
                            element.floatValue = newValue;
                        }
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.indentLevel--;
        }

        private string GetMatrix4x4ElementPath(int col, int row)
        {
            // Unity's Matrix4x4 serializes as e00, e01, e02, e03, e10, e11, etc.
            // where eRC means element at Row R, Column C
            return $"e{row}{col}";
        }

        private string GetVector4ElementPath(int index)
        {
            return index switch
            {
                0 => "x",
                1 => "y",
                2 => "z",
                3 => "w",
                _ => "x"
            };
        }
    }
}
