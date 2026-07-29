/* На основе VCRevitRibbonUtil (Victor Chekalin, https://github.com/chekalin-v/VCRevitRibbonUtil) — без изменений. */

using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace VCRevitRibbonUtil
{
    public class StackedItem : VCRibbonItem
    {
        private readonly Panel _panel;
        private readonly IList<Button> _buttons;

        public StackedItem(Panel panel)
        {
            _panel = panel;
            _buttons = new List<Button>(3);
        }

        public StackedItem CreateButton<TExternalCommandClass>(string name, string text)
            where TExternalCommandClass : class, IExternalCommand
        {
            var commandClassType = typeof(TExternalCommandClass);
            return CreateButton(name, text, commandClassType, null);
        }

        public StackedItem CreateButton<TExternalCommandClass>(string name, string text, Action<Button> action)
            where TExternalCommandClass : class, IExternalCommand
        {
            var commandClassType = typeof(TExternalCommandClass);
            return CreateButton(name, text, commandClassType, action);
        }

        public StackedItem CreateButton(string name, string text, Type externalCommandType)
        {
            return CreateButton(name, text, externalCommandType, null);
        }

        public StackedItem CreateButton(string name, string text, Type externalCommandType, Action<Button> action)
        {
            if (Buttons.Count == 3)
            {
                throw new InvalidOperationException("You cannot create more than three items in the StackedItem");
            }

            var button = new Button(name, text, externalCommandType);
            if (action != null)
            {
                action.Invoke(button);
            }

            Buttons.Add(button);
            return this;
        }

        public int ItemsCount
        {
            get { return Buttons.Count; }
        }

        public IList<Button> Buttons
        {
            get { return _buttons; }
        }
    }
}
