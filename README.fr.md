# Nivalis Performance Fix

*[English version](README.md)*

Un mod BepInEx qui supprime des goulots d'étranglement processeur et des saccades dans **Nivalis Nights**. Dans les
endroits animés (marchés, foule), le jeu est limité par le processeur, pas par la carte graphique. Le mod s'attaque
à ce qui occupe réellement le fil principal du jeu, identifié par profilage (captures PIX et désassemblage).

Mesuré sur un Ryzen 7 9800X3D, marché animé, personnage immobile (banc A/B automatique, 3 manches, même session) :

| | FPS moyen | 1 % les plus bas |
|---|---|---|
| Optimisations désactivées (threads de calcul déjà à 6) | 89,4 | 58,8 |
| **Optimisations activées** | **118,1** | **85,5** |

Soit +32 % de FPS moyen et +45 % sur les 1 % les plus bas, en plus du réglage des threads (+18 % à lui seul : 77,5 → 91,1 FPS).

En marchant en ville, les saccades de plus de 30 ms sont passées de 27 par 90 s à 0 (avec une souris à 1000 Hz, voir
plus bas). Les gains dépendent de ton processeur et de l'endroit où tu es dans le jeu.

## Ce que fait le mod

| Optimisation | Ce qu'elle change | Gain mesuré |
|---|---|---|
| **Threads de calcul** | Règle le nombre de threads de calcul d'Unity selon ton processeur (cœurs physiques − 2, entre 2 et 8) dans `boot.config`. Le jeu réveille ces threads des milliers de fois par seconde : trop de threads coûtent plus qu'ils n'aident. | +18 à 21 % de FPS |
| **Animations à distance** | Les personnages lointains ou hors écran mettent à jour leur animation toutes les quelques images au lieu de chaque image (les plus proches ne changent pas). | +7 % de FPS, 1 % les plus bas +28 % |
| **Caméras secondaires** | Les caméras du ciel et des traces dans la neige font leur rendu une image sur 2 / sur 4. Aucune différence visible. | +8 % de FPS, 1 % les plus bas +47 % |
| **Emploi du temps des PNJ** | Les PNJ simulés revérifient leur emploi du temps toutes les 8 images au lieu de chaque image. | +2,5 % de FPS |
| **Chemins de navigation** | Les PNJ qui marchent relisent leur chemin toutes les 4 images au lieu de le recopier à chaque image. | +2,3 % de FPS, 1 % les plus bas +22 % |
| **Fréquence du ramasse-miettes** | Le ramasse-miettes (GC) passe environ 4 fois moins souvent : sa saccade d'environ 20 ms arrive toutes les ~18 s au lieu de ~4 s. Utilise un peu plus de mémoire. | 4 fois moins de saccades GC |
| **Compatibilité Tracked Quests HUD** | Uniquement avec le mod *Tracked Quests HUD* de Hvizeu : évite la reconstruction du HUD des quêtes quand aucune quête n'est épinglée (saccade de 40 à 60 ms toutes les ~20 s pour un HUD vide). | supprime ces saccades |

Rien ne change au gameplay ni aux sauvegardes. Chaque optimisation peut être désactivée dans la config.

## Prérequis

- Nivalis Nights (Steam, Windows)
- [BepInEx 6 IL2CPP, bleeding edge](https://builds.bepinex.dev/projects/bepinex_be) (testé avec la build 788), lancer le jeu une fois après l'installation

## Installation

1. Extraire l'archive dans le dossier du jeu (celui qui contient `Nivalis Nights.exe`), pour obtenir
   `BepInEx/plugins/NivalisPerformanceFix/NivalisPerformanceFix.dll`.
2. Lancer le jeu. La console BepInEx affiche :
   `Nivalis Performance Fix 1.0.0: 6/6 optimizations active`
3. **La première fois seulement** : la console demande de **relancer le jeu**. Le nombre de threads est lu au
   démarrage du moteur, il ne s'applique donc qu'au lancement suivant.

Après une mise à jour du jeu ou une vérification des fichiers par Steam, `boot.config` est remis à zéro. Le mod réécrit
le réglage et redemande une relance.

## Configuration

`BepInEx/config/hoho92.nivalisperformancefix.cfg` (créé au premier lancement) :

- `[General] Enabled` : interrupteur général.
- Une section par optimisation avec `Enabled` et ses réglages (`[Animation]`, `[Agents]`, `[Navigation]`,
  `[Cameras]`, `[GarbageCollector]`, `[QuestHud]`).
- `[JobWorkers] Mode` : `Auto` (par défaut), `Manual` (utilise `Count`) ou `Off` (ne touche jamais à `boot.config`).

Les valeurs par défaut sont celles qui ont donné les meilleurs résultats.

## Conseils

- **Fréquence de la souris** : à 2000 Hz et plus, Unity traite chaque message de la souris sur le fil principal. En
  tournant la caméra, 1000 Hz au lieu de 2000 Hz a donné +6 à 9 % de FPS. 1000 Hz suffit largement.
- L'overlay Steam n'a pas de coût mesurable.

## Compatibilité

- **Tracked Quests HUD** (Hvizeu) : pris en charge, voir plus haut. L'ordre de chargement n'a pas d'importance.
- Autres mods : aucun conflit connu. Le mod modifie `Character.LateUpdateAll` (postfix),
  `ActiveJournalEntriesUi.Refresh` (prefix, seulement avec Tracked Quests HUD) et trois appels dans le code du jeu.
- Si une mise à jour du jeu change le code visé par un patch, cette optimisation **se désactive d'elle-même** et la
  console indique laquelle : `... disabled: code signature found 0 times (game update?)`. Le jeu continue de
  fonctionner ; vérifie s'il existe une mise à jour du mod.

## Désinstallation

Supprimer `BepInEx/plugins/NivalisPerformanceFix`. Pour remettre le réglage d'origine des threads : soit mettre
d'abord `[JobWorkers] Mode = Off` puis remplacer `Nivalis Nights_Data/boot.config` par le `boot.config.npf-backup`
à côté, soit utiliser *Vérifier l'intégrité des fichiers du jeu* dans Steam.

## Pour les développeurs

Les outils de développement sont désactivés par défaut (`[Developer] Enabled = true` pour les utiliser) :

- **F8** active ou désactive tout le mod ; **F9** lance un banc A/B automatique de `BenchTarget` (rester immobile dans
  un endroit animé) ; **F10** mesure les temps d'image pendant 20 s.
- **Journal de jeu** : une ligne par minute avec les statistiques et le contexte, chaque saccade, et **F11** pour
  marquer une saccade ressentie. Écrit dans `BepInEx/NivalisPerformanceFix.playlog.log` (local uniquement). Résumé
  avec `python tools/analyze_playlog.py`.

### Compilation

Nécessite le SDK .NET 6 et le jeu avec BepInEx installé et lancé une fois (pour les assemblies d'interop).

```
dotnet build src/NivalisPerformanceFix -c Release -p:GameDir="C:\chemin\vers\Nivalis Nights"
```

Une compilation Release copie la DLL dans le jeu (`-p:InstallToGame=false` pour l'éviter). `tools/package.ps1`
crée l'archive de publication dans `dist/`.

## Licence

[MIT](LICENSE) © hoho92
