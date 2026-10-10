using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /**
     * NuGet-style "Package Sources" grid, hosted inside Tools > Options > Conan Package
     * Manager > Package Sources (see ConanPackageSourcesOptionsPage). Unlike
     * NuGet's sources - which NuGet itself stores in NuGet.Config - Conan's remotes are entirely
     * owned and persisted by Conan itself (`conan remote add/remove/update/enable/disable`), so
     * every action here just shells out to the same CLI a terminal would use, then re-reads
     * `conan remote list` to refresh the grid. There is no separate plugin-side storage to keep in
     * sync - Conan's own remotes.json is the single source of truth.
     */
    public partial class ConanPackageSourcesControl : UserControl
    {
        private readonly ObservableCollection<ConanRemoteHelper.ConanRemote> _remotes =
            new ObservableCollection<ConanRemoteHelper.ConanRemote>();

        private ConanRemoteHelper.ConanRemote _selected;

        public ConanPackageSourcesControl()
        {
            InitializeComponent();
            RemotesDataGrid.ItemsSource = _remotes;

            // This control is hosted inside the Tools > Options dialog via an ElementHost/WinForms
            // bridge - a separate WPF tree that the shell's merged theme resource dictionaries
            // never reach, so every DynamicResource lookup in this control's XAML (VsBrushes.WindowKey
            // and friends) silently fails to resolve. Trying to track Visual Studio's actual current
            // theme here (via VSColorTheme.GetThemedColor) turned out unreliable in this specific
            // hosting context - it kept coming back dark regardless of the user's theme. Since every
            // other page of this plugin (the main tool window, etc.) already renders in light mode
            // for this user, this page is simply forced to the same fixed light palette instead of
            // trying (and failing) to dynamically match the shell's theme.
            ApplyLightTheme();

            Loaded += (s, e) => _ = RefreshRemotesAsync();
        }

        /**
         * Injects a fixed light color palette as local resources under the same VsBrushes keys the XAML's DynamicResource bindings reference (see the constructor's remarks for why).
         */
        private void ApplyLightTheme()
        {
            var windowBrush = new SolidColorBrush(Colors.White);
            var textBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            var borderBrush = new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3));
            var highlightBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            var highlightTextBrush = new SolidColorBrush(Colors.White);

            windowBrush.Freeze();
            textBrush.Freeze();
            borderBrush.Freeze();
            highlightBrush.Freeze();
            highlightTextBrush.Freeze();

            // Overriding the resource under the SAME key VsBrushes.XxxKey the XAML's
            // {DynamicResource {x:Static vsshell:VsBrushes.XxxKey}} bindings use lets those
            // lookups resolve locally against this control's own Resources dictionary, instead of
            // needing to reach the (unreachable, from this ElementHost-hosted tree) shell tree.
            Resources[VsBrushes.WindowKey] = windowBrush;
            Resources[VsBrushes.WindowTextKey] = textBrush;
            Resources[VsBrushes.ActiveBorderKey] = borderBrush;
            Resources[VsBrushes.HighlightKey] = highlightBrush;
            Resources[VsBrushes.HighlightTextKey] = highlightTextBrush;

            Background = windowBrush;
            Foreground = textBrush;
        }

        /**
         * Reloads the grid from `conan remote list` + `conan remote list-users`, preserving the current selection by name if it still exists afterwards.
         */
        private async Task RefreshRemotesAsync()
        {
            var remotes = await ConanRemoteHelper.ListRemotesAsync();
            var users = await ConanRemoteHelper.ListRemoteUsersAsync();

            foreach (var remote in remotes)
            {
                remote.User = users.TryGetValue(remote.Name, out string user) ? user : null;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string selectedName = _selected?.Name;

            _remotes.Clear();
            foreach (var remote in remotes)
            {
                _remotes.Add(remote);
            }

            RemotesDataGrid.SelectedItem = string.IsNullOrEmpty(selectedName)
                ? null
                : _remotes.FirstOrDefaultByName(selectedName);
        }

        /**
         * Enables/disables the action buttons and updates their labels ("Activer"/"Désactiver", "Se connecter..."/"Se déconnecter") based on the newly-selected remote.
         */
        private void RemotesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = RemotesDataGrid.SelectedItem as ConanRemoteHelper.ConanRemote;

            bool hasSelection = _selected != null;
            LoginButton.IsEnabled = hasSelection;
            EnableDisableButton.IsEnabled = hasSelection;
            EditSourceButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
            EnableDisableButton.Content = hasSelection && !_selected.Enabled ? "Activer" : "Désactiver";
            LoginButton.Content = hasSelection && !string.IsNullOrEmpty(_selected.User) ? "Se déconnecter" : "Se connecter...";
        }

        /**
         * The "Se connecter..."/"Se déconnecter" button: if the selected remote already has a
         * logged-in user, confirms and runs `conan remote logout`; otherwise prompts for
         * credentials (CredentialsDialog) and runs `conan remote login` (the
         * password is piped through the child process's stdin, never passed as a command-line
         * argument - see ConanRemoteHelper.LoginRemoteAsync).
         */
        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => LoginButtonClickAsync(sender, e));
        }

        private async Task LoginButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(_selected.User))
            {
                MessageBoxResult confirmLogout = MessageBox.Show(
                    $"Se déconnecter du remote '{_selected.Name}' (utilisateur '{_selected.User}') ?",
                    "Conan C/C++ Package Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirmLogout != MessageBoxResult.Yes)
                {
                    return;
                }

                (bool logoutSuccess, string logoutError) = await ConanRemoteHelper.LogoutRemoteAsync(_selected.Name);
                if (!logoutSuccess)
                {
                    MessageBox.Show($"Impossible de se déconnecter de '{_selected.Name}' :\n{logoutError}", "Conan C/C++ Package Manager",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await RefreshRemotesAsync();
                return;
            }

            (string username, string password)? credentials = CredentialsDialog.Show(
                $"Authentification sur le remote '{_selected.Name}' :", _selected.Name);

            if (credentials == null)
            {
                return;
            }

            (bool loginSuccess, string loginError) = await ConanRemoteHelper.LoginRemoteAsync(_selected.Name, credentials.Value.username, credentials.Value.password);
            if (!loginSuccess)
            {
                MessageBox.Show($"Authentification impossible sur '{_selected.Name}' :\n{loginError}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }

        /**
         * The "Ajouter" button: runs `conan remote add` with the name/URL/SSL fields below the grid, then clears them and refreshes the grid.
         */
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => AddButtonClickAsync(sender, e));
        }

        private async Task AddButtonClickAsync(object sender, RoutedEventArgs e)
        {
            string name = NewNameTextBox.Text?.Trim();
            string url = NewSourceTextBox.Text?.Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Le nom et la source (URL) sont obligatoires.", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            (bool success, string error) = await ConanRemoteHelper.AddRemoteAsync(name, url, VerifySslCheckBox.IsChecked == true);

            if (!success)
            {
                MessageBox.Show($"Impossible d'ajouter le remote '{name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            NewNameTextBox.Text = string.Empty;
            NewSourceTextBox.Text = string.Empty;
            await RefreshRemotesAsync();
        }

        /**
         * The "Modifier la source..." button: prompts for a new URL (SSL verification unchanged) and runs `conan remote update`.
         */
        private void EditSourceButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => EditSourceButtonClickAsync(sender, e));
        }

        private async Task EditSourceButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            string input = MessageBoxWithInput.Show(
                $"Nouvelle source (URL) pour le remote '{_selected.Name}' :",
                "Conan C/C++ Package Manager",
                _selected.Url);

            if (string.IsNullOrWhiteSpace(input) || string.Equals(input.Trim(), _selected.Url, StringComparison.Ordinal))
            {
                return;
            }

            (bool success, string error) = await ConanRemoteHelper.UpdateRemoteUrlAsync(_selected.Name, input.Trim(), _selected.VerifySsl);

            if (!success)
            {
                MessageBox.Show($"Impossible de mettre à jour le remote '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }

        /**
         * The "Supprimer" button: confirms, then runs `conan remote remove` (this only removes the remote configuration, never any already-downloaded packages).
         */
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => RemoveButtonClickAsync(sender, e));
        }

        private async Task RemoveButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                $"Supprimer le remote '{_selected.Name}' ? Cette action ne supprime que la configuration du remote, pas les packages déjà installés localement.",
                "Conan C/C++ Package Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            (bool success, string error) = await ConanRemoteHelper.RemoveRemoteAsync(_selected.Name);

            if (!success)
            {
                MessageBox.Show($"Impossible de supprimer le remote '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _selected = null;
            await RefreshRemotesAsync();
        }

        /**
         * The "Activer"/"Désactiver" button: toggles the remote via `conan remote enable`/`disable` without removing its configuration.
         */
        private void EnableDisableButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(() => EnableDisableButtonClickAsync(sender, e));
        }

        private async Task EnableDisableButtonClickAsync(object sender, RoutedEventArgs e)
        {
            if (_selected == null)
            {
                return;
            }

            (bool success, string error) = _selected.Enabled
                ? await ConanRemoteHelper.DisableRemoteAsync(_selected.Name)
                : await ConanRemoteHelper.EnableRemoteAsync(_selected.Name);

            if (!success)
            {
                MessageBox.Show($"Opération impossible sur '{_selected.Name}' :\n{error}", "Conan C/C++ Package Manager",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await RefreshRemotesAsync();
        }
    }

    /**
     * Small WinForms prompt (username + masked password) for `conan remote login`, in the same
     * spirit as MessageBoxWithInput (ConanSharedModels.cs) but with a
     * second, masked field - a plain WPF dialog would need its own Window/theme wiring for very
     * little benefit here, since this only ever pops up modally from Tools > Options.
     */
    internal static class CredentialsDialog
    {
        public static (string username, string password)? Show(string message, string remoteName, string defaultUsername = null)
        {
            var form = new System.Windows.Forms.Form
            {
                Text = "Conan C/C++ Package Manager",
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new System.Drawing.Size(340, 170)
            };

            var messageLabel = new System.Windows.Forms.Label
            {
                Text = message,
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(310, 0),
                Left = 12,
                Top = 10
            };
            var usernameLabel = new System.Windows.Forms.Label { Text = "Utilisateur :", Left = 12, Top = 48, AutoSize = true };
            var usernameTextBox = new System.Windows.Forms.TextBox { Left = 110, Top = 45, Width = 210, Text = defaultUsername ?? string.Empty };
            var passwordLabel = new System.Windows.Forms.Label { Text = "Mot de passe :", Left = 12, Top = 78, AutoSize = true };
            var passwordTextBox = new System.Windows.Forms.TextBox { Left = 110, Top = 75, Width = 210, UseSystemPasswordChar = true };
            var okButton = new System.Windows.Forms.Button { Text = "Se connecter", Left = 145, Width = 90, Top = 115, DialogResult = System.Windows.Forms.DialogResult.OK };
            var cancelButton = new System.Windows.Forms.Button { Text = "Annuler", Left = 240, Width = 80, Top = 115, DialogResult = System.Windows.Forms.DialogResult.Cancel };

            form.Controls.Add(messageLabel);
            form.Controls.Add(usernameLabel);
            form.Controls.Add(usernameTextBox);
            form.Controls.Add(passwordLabel);
            form.Controls.Add(passwordTextBox);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            System.Windows.Forms.DialogResult result = form.ShowDialog();
            if (result != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(usernameTextBox.Text))
            {
                return null;
            }

            return (usernameTextBox.Text.Trim(), passwordTextBox.Text);
        }
    }

    /**
     * Small lookup helper used by ConanPackageSourcesControl.RefreshRemotesAsync to re-find the previously-selected remote by name after reloading the grid.
     */
    internal static class ConanRemoteCollectionExtensions
    {
        /**
         * Case-insensitive lookup by ConanRemoteHelper.ConanRemote.Name.
         */
        public static ConanRemoteHelper.ConanRemote FirstOrDefaultByName(
            this ObservableCollection<ConanRemoteHelper.ConanRemote> remotes, string name)
        {
            foreach (var remote in remotes)
            {
                if (string.Equals(remote.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return remote;
                }
            }

            return null;
        }
    }
}
