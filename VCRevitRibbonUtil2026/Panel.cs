/* На основе VCRevitRibbonUtil (Victor Chekalin, https://github.com/chekalin-v/VCRevitRibbonUtil) — без изменений. */

using System;
using Autodesk.Revit.UI;

namespace VCRevitRibbonUtil
{
    public class Panel : VCRibbonItem
    {
        private readonly Tab _tab;
        private readonly RibbonPanel _panel;

        public Panel(Tab tab, RibbonPanel panel)
        {
            _tab = tab;
            _panel = panel;
        }

        internal RibbonPanel Source
        {
            get { return _panel; }
        }

        internal Tab Tab
        {
            get { return _tab; }
        }

        /// <summary>Create new Stacked items at the panel</summary>
        public Panel CreateStackedItems(Action<StackedItem> action)
        {
            if (action == null) throw new ArgumentNullException("action");

            StackedItem stackedItem = new StackedItem(this);

            action.Invoke(stackedItem);

            if (stackedItem.ItemsCount < 2 ||
                stackedItem.ItemsCount > 3)
            {
                throw new InvalidOperationException("You must create 2 or three items in the StackedItems");
            }

            var item1 = stackedItem.Buttons[0].Finish();
            var item2 = stackedItem.Buttons[1].Finish();
            if (stackedItem.ItemsCount == 3)
            {
                var item3 = stackedItem.Buttons[2].Finish();
                _panel.AddStackedItems(item1, item2, item3);
            }
            else
            {
                _panel.AddStackedItems(item1, item2);
            }

            return this;
        }

        public Panel CreateButton<TExternalCommandClass>(string name, string text)
            where TExternalCommandClass : class, IExternalCommand
        {
            return CreateButton<TExternalCommandClass>(name, text, null);
        }

        public Panel CreateButton<TExternalCommandClass>(string name, string text, Action<Button> action)
            where TExternalCommandClass : class, IExternalCommand
        {
            var commandClassType = typeof(TExternalCommandClass);
            return CreateButton(name, text, commandClassType, action);
        }

        public Panel CreateButton(string name, string text, Type externalCommandType)
        {
            return CreateButton(name, text, externalCommandType, null);
        }

        public Panel CreateButton(string name, string text, Type externalCommandType, Action<Button> action)
        {
            Button button = new Button(name, text, externalCommandType);
            if (action != null)
            {
                action.Invoke(button);
            }

            var buttonData = button.Finish();

            _panel.AddItem(buttonData);

            return this;
        }

        public Panel CreatePullDownButton(string name, string text, Action<PulldownButton> action)
        {
            PulldownButton button = new PulldownButton(name, text);

            if (action != null)
            {
                action.Invoke(button);
            }

            var buttonData = button.Finish();

            var ribbonItem = _panel.AddItem(buttonData) as Autodesk.Revit.UI.PulldownButton;

            button.BuildButtons(ribbonItem);

            button.RibbonItem = ribbonItem;

            return this;
        }

        /// <summary>Create separator on the panel</summary>
        public Panel CreateSeparator()
        {
            _panel.AddSeparator();
            return this;
        }
    }
}
