# Extension Conan pour Visual Studio (Conan 2)

![Build](https://github.com/gandybruno/conan-vs-extension/actions/workflows/main.yml/badge.svg?branch=develop)

[English](README.md) | **Français**

Conan, le gestionnaire de paquets C et C++, directement dans Visual Studio. Parcourez les
bibliothèques de vos remotes Conan, vérifiez qu'elles conviennent à votre projet, installez-les,
travaillez sur leurs sources et déboguez-les - sans quitter l'IDE.

- 🔧 **Conan 2 uniquement** (2.0 et supérieur).
- 💻 **Visual Studio 2022 et 2026**, projets C++ (MSBuild / `.vcxproj`).
- 🌍 **Français et anglais :** l'extension suit la langue de l'interface de Visual Studio.
- 📦 Compatible ConanCenter, Artifactory, serveur Conan et **GitLab Package Registry**.

## Sommaire

1. [Installation](#1-installation)
2. [Premiers pas](#2-premiers-pas)
3. [Parcourir et installer des bibliothèques](#3-parcourir-et-installer-des-bibliothèques)
4. [Vérification de compatibilité avant installation](#4-vérification-de-compatibilité-avant-installation)
5. [Retirer une bibliothèque d'un projet](#5-retirer-une-bibliothèque-dun-projet)
6. [Modifier et déboguer une bibliothèque (mode éditable)](#6-modifier-et-déboguer-une-bibliothèque-mode-éditable)
7. [Remotes (sources de paquets)](#7-remotes-sources-de-paquets)
8. [Ajouter un remote, y compris GitLab](#8-ajouter-un-remote-y-compris-gitlab)
9. [Travailler hors ligne](#9-travailler-hors-ligne)
10. [Intégration dans votre projet](#10-intégration-dans-votre-projet)
11. [Panneau Output et dépannage](#11-panneau-output-et-dépannage)
12. [Paramètres](#12-paramètres)
13. [Compiler l'extension et publier une version](#13-compiler-lextension-et-publier-une-version)

## 1. Installation

**Prérequis**

- Visual Studio 2022 ou plus récent avec la charge de travail *Développement Desktop en C++*.
- [Conan 2](https://docs.conan.io/2/installation.html) installé et accessible dans le `PATH`
  (`conan --version` doit fonctionner dans un terminal). S'il est installé ailleurs, vous
  pouvez indiquer son chemin à l'extension, voir [Paramètres](#12-paramètres).

**Installer l'extension**

- Depuis Visual Studio : **Extensions > Gérer les extensions**, recherchez *Conan*,
  **Télécharger**, puis redémarrez Visual Studio.
- Depuis une version publiée : téléchargez `conan-vs-extension.vsix` depuis les
  [releases GitHub](https://github.com/gandybruno/conan-vs-extension/releases), fermez Visual
  Studio et double-cliquez sur le fichier.
- Depuis les sources : voir [Compiler l'extension](#13-compiler-lextension-et-publier-une-version).

**Il n'y a rien à configurer** si `conan` est dans votre `PATH` : l'extension l'utilise et
sélectionne **conancenter** comme source de paquets par défaut.

## 2. Premiers pas

Ouvrez une solution C++, puis faites un clic droit sur un projet dans l'Explorateur de
solutions - ou utilisez le menu **Projet** - et choisissez **Manage Conan Packages...**.

![Menu Projet](.github/readme/fr-project-menu.png)

Une fenêtre de document dédiée à ce projet s'ouvre, avec un onglet **Parcourir** (tout ce que
la source sélectionnée propose) et un onglet **Installé** (ce que le projet utilise déjà).

> L'entrée **Actualiser et parcourir la base de données** juste au-dessus est une commande de
> Visual Studio (voir [IntelliSense](#intellisense)).

## 3. Parcourir et installer des bibliothèques

![Onglet Parcourir](.github/readme/fr-browse.png)

- **Source** (en haut à droite) : le remote Conan dont vient la liste. Les remotes sont ceux
  configurés dans Conan (`conan remote list`). Le bouton ⚙ ouvre les paramètres de l'extension
  et le bouton 🔄 actualise la liste.
- **Zone de recherche** (Ctrl+L) : la liste se filtre pendant la saisie. Entrée, ou la loupe,
  mémorise le terme dans la liste déroulante des *recherches récentes*.
- **Panneau de détails** : choix de la version, description, licence, auteur, tags, site
  officiel et [vérification de compatibilité](#4-vérification-de-compatibilité-avant-installation).
- **Installer** : ajoute la bibliothèque au projet et lance `conan install` immédiatement, si
  bien que la prochaine compilation a déjà tout ce qu'il lui faut. Un voile de progression
  s'affiche (Conan peut devoir compiler un binaire manquant, ce qui peut prendre plusieurs
  minutes) et la sortie complète des commandes défile dans le panneau **Conan** de la fenêtre
  Output.

La première fois, l'extension crée `conanfile.py` et `conandata.yml` à côté du fichier projet,
puis tient `conandata.yml` à jour au fil des ajouts et retraits.

## 4. Vérification de compatibilité avant installation

Quand vous sélectionnez une bibliothèque/version, l'extension demande à Conan
(`conan graph info`) si le profil du projet - version du compilateur, type de build, runtime,
standard C++ - peut l'utiliser, **sans rien installer ni compiler** :

| Message | Signification |
|---|---|
| ✔ *Un binaire correspond à ce projet (msvc 194, Debug).* | Un binaire précompilé existe (dans le cache ou sur le remote). |
| ⚠ *Aucun binaire précompilé... compilée à partir des sources incluses dans le paquet.* | Conan va compiler ; les sources voyagent avec la recette (fonctionne hors ligne). |
| ⚠ *... la recette télécharge ses sources depuis internet.* | Conan va compiler ; une connexion réseau est nécessaire pour les sources. |
| ⛔ *Incompatible avec ce projet : ...* | La recette refuse cette configuration, ou il n'y a ni binaire **ni** source. **Installer est désactivé** et la raison est affichée. |
| *Compatibilité non vérifiable.* | Conan n'a pas pu répondre (hors ligne, erreur). Installer reste actif. |

Sous le message, les **binaires publiés sur le remote** pour cette version sont listés
(compilateur, type de build, runtime, architecture - Windows uniquement), pour voir d'un coup
d'œil quelles configurations existent. Les bibliothèques header-only sont indiquées comme
indépendantes du compilateur.

> Le `compiler.version` du profil généré vient du toolset MSVC réellement utilisé par votre
> projet (par exemple MSVC 14.44 → `194`), pour correspondre au compilateur qui compilera la
> bibliothèque. Quand le toolset de plateforme du projet diffère de celui que votre Visual
> Studio fournit par défaut, une note est écrite dans le panneau Output Conan.

## 5. Retirer une bibliothèque d'un projet

![Onglet Installé avec un paquet éditable](.github/readme/fr-installed-editable.png)

L'onglet **Installé** liste les dépendances du projet courant, lues dans son `conandata.yml` -
y compris les bibliothèques que la source sélectionnée ne connaît pas (paquet local ou
éditable, paquet d'un autre remote).

**Retirer du projet** supprime la dépendance de `conandata.yml` et relance `conan install`.
Le paquet **reste dans le cache Conan** : vos autres projets ne sont pas affectés. Pour vider
le cache, voir [Nettoyer le cache](#nettoyer-le-cache-local).

## 6. Modifier et déboguer une bibliothèque (mode éditable)

Les *paquets éditables* de Conan permettent à un projet d'utiliser les sources d'une
bibliothèque situées dans un dossier local plutôt que le paquet du cache, afin de modifier la
bibliothèque et de la déboguer.

1. Dans l'onglet **Installé**, sélectionnez la bibliothèque.
2. Cochez **Éditable (sources locales)** et choisissez le dossier qui contient le
   `conanfile.py` de la bibliothèque.
3. L'extension lance `conan editable add` puis `conan install` ; le projet se compile
   désormais contre vos sources locales. Le dossier est affiché sous le nom, un ✎ marque les
   paquets éditables dans la liste et **Dossier...** permet de le changer.
4. Décochez la case pour revenir au paquet normal.

Prérequis (de Conan, pas de l'extension) :

- la recette doit définir `layout()` ;
- la bibliothèque doit avoir été compilée une fois dans son propre dossier (`conan build .`)
  pour que ses fichiers générés existent, dans la même configuration que votre projet
  (Debug/Release...).

Le mode éditable est enregistré dans le Conan home : il s'applique à **tous** les projets de
la machine qui utilisent ce `nom/version`. L'état est relu depuis Conan à chaque affichage.

## 7. Remotes (sources de paquets)

**Outils > Options > Conan Package Manager > Package Sources** (ou le bouton ⚙ du gestionnaire)
est une grille de type NuGet des remotes configurés sur la machine - la même liste que
`conan remote list`.

![Package Sources](.github/readme/fr-package-sources.png)

| Bouton | Commande équivalente |
|---|---|
| **Ajouter un remote...** | `conan remote add` (voir [ci-dessous](#8-ajouter-un-remote-y-compris-gitlab)) |
| **Se connecter... / Se déconnecter** | `conan remote login` / `conan remote logout` |
| **Activer / Désactiver** | `conan remote enable` / `conan remote disable` |
| **Modifier la source...** | `conan remote update` |
| **Supprimer** | `conan remote remove` (uniquement la configuration, pas les paquets) |

La colonne **Utilisateur** indique qui est authentifié sur chaque remote. Les mots de passe
sont transmis à `conan` par son environnement, jamais en ligne de commande. La liste
déroulante **Source** du gestionnaire suit les modifications faites ici.

### Nettoyer le cache local

**Nettoyer le cache...** (en bas à droite de la page) exécute `conan remove "*" -c` après
confirmation : tous les paquets et binaires téléchargés ou compilés jusqu'ici, pour tous les
projets, sont supprimés et seront retéléchargés ou recompilés au prochain `conan install`. Les
paquets éditables ne sont pas touchés.

## 8. Ajouter un remote, y compris GitLab

Cliquez sur **Ajouter un remote...**.

![Ajouter un remote](.github/readme/fr-add-remote.png)

**Remote Conan (URL)** - Artifactory, serveur Conan... : indiquez un *nom* et l'*URL*.

**GitLab Package Registry** - renseignez :

| Champ | Valeur |
|---|---|
| Nom | Un nom au choix, par exemple `gitlab` |
| Serveur GitLab | `https://gitlab.com` ou votre propre serveur |
| ID ou chemin du projet | L'ID numérique du projet (*Paramètres > Général*) ou `groupe/projet` |
| Utilisateur | Votre nom d'utilisateur GitLab (projet privé) |
| Token d'accès | Un token personnel, de projet ou de déploiement avec les scopes **`read_api`** et **`read_package_registry`** (projet privé) |

La boîte de dialogue construit pour vous l'URL Conan v2
`<serveur>/api/v4/projects/<id>/packages/conan` et, si un utilisateur et un token sont donnés,
vous authentifie. Laissez-les vides pour un projet public.

Le même résultat depuis un terminal :

```
conan remote add gitlab https://gitlab.com/api/v4/projects/<id-du-projet>/packages/conan
conan remote login gitlab <utilisateur> -p <token>
```

Le point d'accès Conan de GitLab ne gère pas la recherche par joker seul ; pour les remotes
GitLab, l'extension interroge donc aussi l'
[API packages](https://docs.gitlab.com/ee/api/packages.html#for-a-project) de GitLab pour
lister les noms de bibliothèques ; le token est mémorisé par remote dans ce but.

Pour publier un paquet sur ce remote : `conan upload "<nom>/<version>" -r=gitlab`.

## 9. Travailler hors ligne

Si le remote sélectionné est injoignable, la liste bascule sur le **cache Conan local** (les
paquets déjà téléchargés) et un bandeau l'indique. Installer une bibliothèque dont la recette
et le binaire sont dans le cache fonctionne sans connexion. La vérification de compatibilité
indique simplement qu'elle n'a pas pu être faite et ne bloque jamais l'installation dans ce
cas.

## 10. Intégration dans votre projet

Fichiers écrits à côté du `.vcxproj` :

| Fichier | Rôle |
|---|---|
| `conandata.yml` | La liste des dépendances (`requirements:`). Modifié par l'extension. |
| `conanfile.py` | Une recette générée qui installe ce que liste `conandata.yml` et génère les feuilles de propriétés MSBuild. |
| `.conan/<Config>_<Plateforme>` | Un profil Conan par configuration de build, généré depuis le projet (arch, type de build, version du compilateur, runtime, standard C++). |
| `.conan/conan_install.bat` | Étape de pré-génération qui relance `conan install` quand quelque chose a changé. |
| `conan/**/*.props` | Les feuilles de propriétés générées par Conan ; importées par le projet pour renseigner les chemins d'include et de bibliothèques. |

`conanfile.py` et `conandata.yml` commencent par un commentaire de garde : si vous le
supprimez, l'extension cesse de modifier le fichier et vous pouvez l'éditer à la main.

Comportements automatiques :

- `conan install` s'exécute de façon proactive à l'ouverture d'une solution, à l'ajout d'un
  projet ou au changement de configuration/profil actif. Les installations sont sérialisées
  pour que deux d'entre elles n'entrent jamais en concurrence sur le cache Conan.
- Si vous lancez une **génération** pendant que Conan installe, elle est reportée puis
  relancée automatiquement quand Conan a terminé.
- Chaque appel à `conan` est borné par un délai : une commande bloquée ne fige jamais l'IDE.
- Une recette laissée corrompue dans le cache Conan est supprimée et l'installation est
  retentée une fois.

### IntelliSense

Des fichiers `conandeps.props` vides sont créés avant le premier `conan install` et importés
sans condition, et quand une installation modifie les feuilles de propriétés générées,
l'extension exécute pour vous **Projet > Actualiser et parcourir la base de données**
(*Rescan Solution*) : les `#include` d'une bibliothèque tout juste installée sont résolus sans
recharger le projet.

## 11. Panneau Output et dépannage

Chaque commande `conan` lancée par l'extension - installations, listes, opérations sur les
remotes, lecture des recettes, vérifications de compatibilité - est journalisée dans le panneau
**Conan** de la fenêtre Output (**Affichage > Sortie**, puis *Conan* dans *Afficher la sortie
à partir de*). Il s'affiche dès que quelque chose est écrit, et la sortie de `conan install`,
compilation comprise, défile en direct.

| Symptôme | À vérifier |
|---|---|
| La fenêtre dit que Conan est introuvable | Installez Conan 2, ou indiquez son chemin dans **Outils > Options > Conan Package Manager > General**. |
| La liste est vide / un bandeau s'affiche | Le remote est injoignable (hors ligne, VPN, mauvaise URL) : le cache est affiché à la place. |
| *Incompatible avec ce projet* | Lisez la raison ; choisissez une autre version ou changez la configuration du projet. |
| Les binaires sont recompilés après un changement de toolset | Les binaires sont liés à `compiler.version` ; consultez la note de toolset dans le panneau Conan ou publiez un binaire pour votre profil. |
| `#include` encore souligné | Lancez une fois **Projet > Actualiser et parcourir la base de données** ; vérifiez que le dossier `conan/` existe. |
| La connexion GitLab échoue | Le token doit avoir `read_api` et `read_package_registry` ; l'erreur exacte est dans le panneau Conan. |

## 12. Paramètres

**Outils > Options > Conan Package Manager > General**

- **Use System Conan** (par défaut) : utilise `conan` du `PATH`.
- **Executable Path** : un `conan.exe` précis. Décochez *Use System Conan* pour le renseigner.

La source sélectionnée et les tokens GitLab par remote sont enregistrés dans
`%UserProfile%\.conan-vs-extension\plugin-settings.json` (en clair - traitez ce fichier comme
n'importe quel fichier d'identifiants).

## 13. Compiler l'extension et publier une version

Prérequis : Visual Studio avec la charge de travail *Développement d'extensions Visual Studio*.

```
git clone https://github.com/gandybruno/conan-vs-extension.git
cd conan-vs-extension
msbuild conan-vs-extension.csproj /t:Rebuild /p:Configuration=Release
```

Le VSIX est produit dans `bin\Release\conan-vs-extension.vsix`. Appuyez sur F5 dans Visual
Studio pour le déboguer dans l'instance expérimentale.

**Branches (gitflow).** Le travail se fait dans des branches `feature/*` fusionnées dans
`develop` ; `master` ne reçoit que les versions publiées. Les pushs sur `develop` et les pull
requests sont seulement *compilés* par la CI. Un tag `v*` sur un commit de `master` (qui
correspond à la version de `source.extension.vsixmanifest`) génère le VSIX et crée une release
GitHub en brouillon qui le contient.

Voir [CHANGELOG.md](CHANGELOG.md) pour l'historique et [LICENSE](LICENSE) (MIT).
