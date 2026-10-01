using System;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    public interface ITrayWidget
    {
        Task ToggleAsync();
        bool IsVisible { get; }
        void Hide();
    }

    public sealed class TrayWidgetController
    {
        private readonly Func<ITrayWidget> _widgetFactory;
        private ITrayWidget? _widget;

        public TrayWidgetController(Func<ITrayWidget> widgetFactory)
        {
            _widgetFactory = widgetFactory;
        }

        public ITrayWidget? Widget => _widget;

        public Task ToggleAsync()
        {
            _widget ??= _widgetFactory();
            return _widget.ToggleAsync();
        }

        public void HideIfVisible()
        {
            _widget?.Hide();
        }

        public bool IsVisible => _widget?.IsVisible == true;
    }
}
