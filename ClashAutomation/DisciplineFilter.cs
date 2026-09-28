using System;
using System.Linq;
using Autodesk.Navisworks.Api;

namespace ClashAutomation
{
    /// <summary>
    /// Отбор элементов раздела по свойству "Файл источника" с исключением
    /// служебных категорий/имён — та же логика, что и в генераторе допусков
    /// коллизий (Авто-XML/clash_xml_generator.py: PROP_INTERNAL/EXTRA_SETS).
    /// </summary>
    public static class DisciplineFilter
    {
        // Свойство "Тип" (DataPropertyNames.ItemType): категории, элементы
        // которых исключаются из выборки раздела.
        private static readonly string[] ExcludedTypes =
        {
            "Обобщенные модели",
            "Ограждения",
        };

        // Свойство "Имя" (DataPropertyNames.ItemName): элементы, чьё имя
        // содержит любую из этих подстрок, исключаются из выборки раздела.
        private static readonly string[] ExcludedNameSubstrings =
        {
            "перемы",
            "времен",
        };

        public static ModelItemCollection BuildDisciplineSelection(Document doc, string sourceToken)
        {
            var result = new ModelItemCollection();
            if (doc == null || string.IsNullOrWhiteSpace(sourceToken))
                return result;

            foreach (ModelItem item in doc.Models.RootItemDescendantsAndSelf)
            {
                if (!MatchesSourceToken(item, sourceToken))
                    continue;
                if (IsExcluded(item))
                    continue;
                result.Add(item);
            }

            return result;
        }

        private static bool MatchesSourceToken(ModelItem item, string sourceToken)
        {
            string sourceFile = GetStringProperty(item, DataPropertyNames.ItemSourceFile);
            return sourceFile != null &&
                   sourceFile.IndexOf(sourceToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsExcluded(ModelItem item)
        {
            string type = GetStringProperty(item, DataPropertyNames.ItemType);
            if (type != null && ExcludedTypes.Any(
                    t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)))
                return true;

            string name = GetStringProperty(item, DataPropertyNames.ItemName);
            if (name != null && ExcludedNameSubstrings.Any(
                    s => name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            return false;
        }

        private static string GetStringProperty(ModelItem item, string propertyInternalName)
        {
            DataProperty prop = item.PropertyCategories.FindPropertyByName(
                PropertyCategoryNames.Item, propertyInternalName);
            return prop != null ? prop.Value.ToDisplayString() : null;
        }
    }
}
