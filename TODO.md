# TODO

Reste à faire après la revue du 11/10/2026. Les points marqués « à vérifier » viennent de la
lecture du code et n'ont pas été reproduits dans Visual Studio.

## Avant de pousser `develop`

- [ ] Tester dans Visual Studio les corrections récentes :
  - [ ] changer de configuration puis appuyer aussitôt sur F5 : le débogueur doit démarrer
        une fois Conan terminé ;
  - [ ] annuler l'installation d'une bibliothèque qui compile depuis les sources : plus aucun
        `cl.exe` ni `cmake` dans le Gestionnaire des tâches ;
  - [ ] rouvrir une solution déjà installée : plus de `conan install` dans le volet Conan ;
  - [ ] modifier `conandata.yml` à la main puis rouvrir : l'install doit repartir ;
  - [ ] cliquer sur le lien « homepage » d'un paquet.
- [ ] Tester la création de paquet : bibliothèque statique et DLL, Debug et Release, puis
      consommation du paquet dans un autre projet. Cas jamais exécutés :
  - [ ] une DLL (import lib dans `lib/`, DLL et `.pdb` dans `bin/`) ;
  - [ ] la plateforme Win32 ;
  - [ ] une bibliothèque qui a elle-même des dépendances Conan ;
  - [ ] un projet dont la sortie est dans le dossier de la solution (chemin `../x64/Debug`) ;
  - [ ] le bouton Annuler pendant la création et le bouton « Dossier... » ;
  - [ ] l'onglet Paquet avec Visual Studio en français ;
  - [ ] les champs invalides entourés en rouge et le message au-dessus des boutons.
- [ ] Pousser `develop`.
- [ ] Supprimer le stash `stash@{0}` (« WIP on develop »), qui fait doublon avec ce qui est mergé.

## Création de paquet (à vérifier)

- [ ] **Blocage possible pendant la compilation.** `Packaging/VsProjectBuilder.cs` attend la
      fin du build sans délai ni annulation. Si l'événement de fin n'arrive jamais (par exemple
      un build reporté par `BuildEventsHandler`), la création reste bloquée et toute nouvelle
      tentative répond « un paquet est déjà en cours de création » jusqu'au redémarrage de
      Visual Studio.
- [ ] **Pas d'annulation depuis le menu.** « Créer le paquet Conan »
      (`Packaging/CreatePackageCommand.cs`) ne peut pas être annulé ; seul l'onglet Paquet a un
      bouton Annuler.
- [ ] **Révision de recette instable.** `conandata.yml` est exporté avec la recette et contient
      les configurations cochées et les dossiers de sortie : empaqueter Debug puis Release avec
      une sélection différente donne deux révisions de recette, donc des binaires séparés. Un
      dossier de sortie sur un autre lecteur y est écrit en chemin absolu.
- [ ] **`.pdb` des bibliothèques statiques.** La recette cherche `<nom>.pdb` dans le dossier de
      sortie ; pour une bibliothèque statique, Visual Studio l'écrit par défaut dans le dossier
      intermédiaire sous un autre nom (`vc143.pdb`). Il ne serait alors pas empaqueté, sans
      message.
- [ ] **Mauvais projet ciblé.** Sans projet sélectionné dans l'Explorateur de solutions, la
      fenêtre Conan et « Créer le paquet Conan » prennent le projet de démarrage : sur une
      solution bibliothèque + tests, c'est le projet de tests, qui n'a pas d'onglet Paquet.
- [ ] **Annuler ne stoppe pas la compilation.** Le bouton Annuler de l'onglet arrête la commande
      Conan en cours, pas un build Visual Studio déjà lancé, qui va jusqu'au bout.
- [ ] **Version déjà présente dans le cache.** Recréer un paquet avec la même version le
      remplace sans avertissement.
- [ ] **Description et licence absentes dans « Parcourir ».** Pour un paquet produit par
      l'extension, la recette les prend dans `conandata.yml`, alors que l'onglet les cherche
      dans le texte de la recette (voir « Remplacer la lecture des recettes » dans Qualité).
- [ ] **Bibliothèques header-only** non gérées : l'onglet Paquet n'apparaît que pour une
      bibliothèque statique ou une DLL.

## Création de paquet (évolutions)

- [ ] Bouton **Publier** (`conan upload`) avec choix du remote ; aujourd'hui la commande est
      seulement affichée à la fin de la création.
- [ ] Mode **avec sources** (`conan create`), pour qu'un consommateur puisse recompiler une
      configuration qui n'a pas été empaquetée.
- [ ] **Sources dans le paquet pour le débogage** (step into), sans servir à la compilation.
- [ ] **« Utiliser le projet local »** : mode éditable automatique quand la bibliothèque et son
      consommateur sont dans la même solution.
- [ ] Génération d'un **`test_package`**.
- [ ] **Création du paquet à chaque build**, en option.

## Avant de publier sur le Marketplace

- [ ] Choisir l'éditeur : `publisher` vaut toujours `conan-io` dans `vsixManifest.json`.
- [ ] Trancher l'identifiant de l'extension dans `source.extension.vsixmanifest` : il reprend
      celui de l'extension officielle de Conan (`Identity Id`, `Publisher="Conan"`, liens vers
      `conan-io`). Publication sous son propre nom : nouvel identifiant. Contribution au dépôt
      officiel : ne rien changer.
- [ ] Corriger le lien « Français » de `README.md`, cassé sur la page Marketplace (seul
      `README.md` y est publié).
- [ ] Figer le numéro de version et la section `[Unreleased]` de `CHANGELOG.md`.
- [ ] Ajouter des captures d'écran de l'onglet Paquet aux deux README.

## Sécurité

- [ ] Chiffrer les tokens GitLab : ils sont en clair dans
      `%UserProfile%\.conan-vs-extension\plugin-settings.json`, et aussi persistés dans les
      paramètres de Visual Studio par `ConanOptionsPage.GitLabRemoteTokensRaw` (DPAPI ou
      Gestionnaire d'identifiants Windows).
- [ ] Masquer la saisie du token dans `MessageBoxWithInput` (`ConanSharedModels.cs`).

## Bugs restants

- [ ] Sous-menu « Projet uniquement » : un build lancé par là et reporté pendant un install
      Conan retombe sur « Générer la solution » (`BuildEventsHandler.cs`).
- [ ] Deux fenêtres du gestionnaire ouvertes en même temps partagent l'état statique de
      `PackageSubtitleConverter` et le fichier unique `targets-data.json`.
- [ ] `conan remote list` (`ConanRemoteHelper.ListRemotesAsync`) : ajouter un timeout sur la
      lecture et passer à `--format=json` au lieu d'analyser le texte par regex.
- [ ] Un changement fait hors de l'extension qui ne touche aucun fichier du projet (par exemple
      `conan editable add` dans un terminal) n'est pas détecté par le saut du `conan install`.

## Qualité

- [ ] Ajouter un projet de tests. Bons candidats : `PackagePlanner`, `ConanProfileFile`,
      `VersionFile`, `ConanDataFile`, `ConanFileManager.NormalizeReference` et le mapping des
      profils (`ConanProfilesManager`).
- [ ] Faire tourner ces tests dans la CI (`.github/workflows/main.yml`), qui ne fait que compiler.
- [ ] Alléger `ConanPackageManagerControl.xaml.cs` : extraire le catalogue de paquets et un
      ViewModel.
- [ ] Découper `ProjectConfigurationManager.cs`, qui mélange encore le script de pré-build,
      `conan install`, les feuilles de propriétés et la recherche de projets.
- [ ] Ranger les fichiers de la racine par dossier, comme `Packaging/`.
- [ ] Traiter le `FIXME` de `BuildEventsHandler.OnBuildProjConfigDone` : au premier build,
      l'injection de `conandeps.props` arrive après le démarrage de la compilation.
- [ ] Remplacer la lecture des recettes (téléchargement, regex sur le Python, suppression) par
      le JSON de `conan graph info`, déjà appelé pour la compatibilité (à vérifier : présence de
      description, licence, homepage et topics dans ce JSON).
