using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

namespace ClashAutomation
{
    // DeveloperId — GUID вместо 4-символьного ADN-кода (его у нас нет), это
    // допустимо по документации PluginAttribute ("4 character ADN developer
    // code or a GUID").
    [Plugin("ExclusionTest", "8c2bade8-81e1-4658-832f-e1361a167587", DisplayName = "Тест исключений")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class ExclusionTestPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                MessageBox.Show("Нет открытого документа.");
                return 0;
            }

            // На первом прогоне — просто зашитый токен раздела, чтобы
            // проверить логику отдельно от остального пайплайна.
            string sourceToken = "AR01";

            ModelItemCollection result = DisciplineFilter.BuildDisciplineSelection(doc, sourceToken);

            doc.CurrentSelection.CopyFrom(result);

            MessageBox.Show($"Отобрано элементов: {result.Count}\n" +
                             "Проверь в модели: составные объекты категории " +
                             "«Обобщенные модели»/«Ограждения» и с именами " +
                             "«перемы»/«времен» НЕ должны быть выделены.");
            return 0;
        }
    }
}
