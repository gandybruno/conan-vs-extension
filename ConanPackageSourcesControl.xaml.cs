using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace conan_vs_extension
{
    /// <summary>
    /// NuGet-style "Package Sources" grid, hosted inside Tools &gt; Options &gt; Conan Package
    /// Manager &gt; Package Sources (see <see cref="ConanPackageSourcesOptionsPage"/>). Unlike
    /// NuGet's sources - which NuGet itself stores in NuGet.Config - Conan's remotes are entirely
    /// owned and persisted by Conan itself (`conan remote add/remove/update/enable/disable`), so
    /// every action here just shells out to the same CLI a terminal would use, then re-reads
    /// `conan remote list` to refresh the grid. There is no separate plugin-side storage to keep in
    /// sync - Conan's own remotes.json is the single source of truth.
    /// </summary>
    public partial class ConanPackageSourcesControl : UserControl
    {
        private readonly ObservableCollection<ConanRemoteHelper.ConanRemote> _remotes =
            new ObservableCollection<ConanRemoteHelper.ConanRemote>();

        private ConanRemoteHelper.ConanRemote _selected;

        public ConanPackageSourcesControl()
        {
            InitializeComponent();
            RemotesDataGrid.ItemsSource = _remotes;
            Loaded += (s, e) => _ = RefreshRemotesAsync();
        }

        private async Task RefreshRemotesAsync()
        {
            var remotes = await ConanRemoteHelper.ListRemotesAsync();

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

        private void RemotesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = RemotesDataGrid.SelectedItem as ConanRemoteHelper.ConanRemote;

            bool hasSelection = _selected != null;
            EnableDisableButton.IsEnabled = hasSelection;
            EditSourceButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
            EnableDisableButton.Content = hasSelection && !_selected.Enabled ? "Activer" : "Désactiver";
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
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

        private async void EditSourceButton_Click(object sender, RoutedEventArgs e)
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

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
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

        private async void EnableDisableButton_Click(object sender, RoutedEventArgs e)
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

    internal static class ConanRemoteCollectionExtensions
    {
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
