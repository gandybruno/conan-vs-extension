using System.Collections.Generic;

namespace conan_vs_extension
{
    /**
     * One entry of the "libraries" map in targets-data.json.
     */
    public class Library
    {
        //public string Description { get; set; }
        //public List<string> License { get; set; }
        public List<string> Versions { get; set; }
    }

    /**
     * Deserialization root for targets-data.json (see ConanPackageManagerControl's
     * targets-data.json handling).
     */
    public class RootObject
    {
        public Dictionary<string, Library> Libraries { get; set; }
    }

    /**
     * Deserialization/serialization shape for the `requirements:` list in a project's
     * conandata.yml - see ConanFileManager.
     */
    public class Requirements
    {
        public Requirements(string[] requirements)
        {
            this.requirements = requirements;
        }
        public Requirements()
        {
            this.requirements = new string[] { };
        }
        public string[] requirements { get; set; }
    }

    /**
     * Minimal WinForms "one text field" prompt dialog, shared across the extension (see
     * also the two-field CredentialsDialog used by ConanPackageSourcesControl).
     */
    public static class MessageBoxWithInput
    {
        /**
         * Shows a modal prompt with a single text field pre-filled with textDefault.
         *
         * @return The entered text, or null if the dialog was cancelled.
         */
        public static string Show(string message, string title, string textDefault)
        {
            // Création du formulaire
            System.Windows.Forms.Form inputForm = new System.Windows.Forms.Form();
            inputForm.Text = title;

            System.Windows.Forms.Label label = new System.Windows.Forms.Label { Text = message, AutoSize = true, Left = 10, Top = 10 };
            System.Windows.Forms.TextBox textBox = new System.Windows.Forms.TextBox { Left = 10, Top = 40, Width = 260 };
            System.Windows.Forms.Button okButton = new System.Windows.Forms.Button { Text = "OK", Left = 150, Width = 60, Top = 70, DialogResult = System.Windows.Forms.DialogResult.OK };
            System.Windows.Forms.Button cancelButton = new System.Windows.Forms.Button { Text = Loc.T("Cancel", "Annuler"), Left = 220, Width = 60, Top = 70, DialogResult = System.Windows.Forms.DialogResult.Cancel };
            textBox.Text = textDefault;
            inputForm.Controls.Add(label);
            inputForm.Controls.Add(textBox);
            inputForm.Controls.Add(okButton);
            inputForm.Controls.Add(cancelButton);

            inputForm.AcceptButton = okButton;
            inputForm.CancelButton = cancelButton;
            inputForm.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            inputForm.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            inputForm.ClientSize = new System.Drawing.Size(300, 120);

            // Affichage de la boîte
            System.Windows.Forms.DialogResult dialogResult = inputForm.ShowDialog();
            return dialogResult == System.Windows.Forms.DialogResult.OK ? textBox.Text : null;
        }
    }
}
