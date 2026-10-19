using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace bamu
{
    public sealed partial class BridgeWindow
    {
        private void DrawProductCard(Product product, bool linked)
        {
            const float height = 66;
            var card = GUILayoutUtility.GetRect(1, height, GUILayout.Height(height), GUILayout.ExpandHeight(false), GUILayout.ExpandWidth(true));
            GUI.Box(card, GUIContent.none, EditorStyles.helpBox);
            var image = new Rect(card.x + 5, card.y + 9, 48, 48);
            var thumbnail = ThumbnailCache.Get(product);
            if (thumbnail != null) GUI.DrawTexture(image, thumbnail, ScaleMode.ScaleToFit);
            else GUI.Label(image, EditorGUIUtility.IconContent("Prefab Icon"));

            var left = image.xMax + 6;
            var width = Mathf.Max(1, card.xMax - 6 - left);
            var titleStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, clipping = TextClipping.Clip,
                fontStyle = selectedKey == product.key ? FontStyle.Bold : FontStyle.Normal };
            DrawCardText(new Rect(left, card.y + 4, width, 18), product.name ?? product.key, titleStyle);

            var statusWidth = Mathf.Min(104, width);
            var statusX = card.xMax - 6 - statusWidth;
            DrawCardStatus(new Rect(statusX, card.y + 25, statusWidth, 17), "Assetリンク", linked,
                "このプロジェクトにリンク記録があるかを表示します。");
            DrawCardStatus(new Rect(statusX, card.y + 43, statusWidth, 17), "BLM保存", !string.IsNullOrEmpty(product.directory),
                "BLMの保存先が登録されているかを表示します。\n" + UnifiedCatalog.State(product, BoothSyncStatusCache.Latest));
            DrawCardText(new Rect(left, card.y + 43, Mathf.Max(1, width - statusWidth - 5), 17), product.shop ?? "", EditorStyles.miniLabel);

            HandleDrop(card, product);
            EditorGUIUtility.AddCursorRect(card, MouseCursor.Link);
            if (GUI.Button(card, GUIContent.none, GUIStyle.none)) SelectProduct(product.key);
            GUILayout.Space(4);
        }

        private static void DrawCardText(Rect rect, string text, GUIStyle style)
        {
            var line = text.Replace('\r', ' ').Replace('\n', ' ');
            if (style.CalcSize(new GUIContent(line)).x > rect.width)
            {
                // Use text-element boundaries so truncation never splits an emoji or combining character.
                var boundaries = StringInfo.ParseCombiningCharacters(line);
                int low = 0, high = boundaries.Length;
                while (low < high)
                {
                    var mid = (low + high + 1) / 2;
                    var end = mid == boundaries.Length ? line.Length : boundaries[mid];
                    if (style.CalcSize(new GUIContent(line.Substring(0, end) + "…")).x <= rect.width) low = mid;
                    else high = mid - 1;
                }
                line = line.Substring(0, low == boundaries.Length ? line.Length : boundaries[low]) + "…";
            }
            GUI.Label(rect, new GUIContent(line, text), style);
        }

        private static void DrawCardStatus(Rect rect, string label, bool value, string tooltip)
        {
            GUI.Label(new Rect(rect.x, rect.y, Mathf.Max(1, rect.width - 22), rect.height), new GUIContent(label, tooltip), EditorStyles.miniLabel);
            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
            style.normal.textColor = value
                ? (EditorGUIUtility.isProSkin ? new Color(.35f, .85f, .45f) : new Color(.1f, .48f, .2f))
                : (EditorGUIUtility.isProSkin ? new Color(1f, .4f, .4f) : new Color(.78f, .12f, .12f));
            GUI.Label(new Rect(rect.xMax - 18, rect.y, 18, rect.height), new GUIContent(value ? "✓" : "×", tooltip), style);
        }
    }
}
