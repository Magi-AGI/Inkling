using UnityEngine;
using UnityEditor;

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

        private bool showProductMatrix = true;
        private bool showReactionImpulseMatrix = true;

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
