using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace GmodAddonManager.UI.Views
{
    public partial class ResetChoiceDialog : Window
    {
        public enum ResetChoice
        {
            Cancel,
            ResetAll,
            ResetCurrentOnly
        }

        public ResetChoice Result { get; private set; } = ResetChoice.Cancel;

        public ResetChoiceDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            
            var cancelButton = this.FindControl<Button>("CancelButton");
            var resetAllButton = this.FindControl<Button>("ResetAllButton");
            var resetCurrentButton = this.FindControl<Button>("ResetCurrentButton");
            
            if (cancelButton != null)
                cancelButton.Click += (s, e) => Close();
                
            if (resetAllButton != null)
                resetAllButton.Click += (s, e) =>
                {
                    Result = ResetChoice.ResetAll;
                    Close();
                };
                
            if (resetCurrentButton != null)
                resetCurrentButton.Click += (s, e) =>
                {
                    Result = ResetChoice.ResetCurrentOnly;
                    Close();
                };
        }
    }
}