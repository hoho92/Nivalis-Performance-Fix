![Nivalis Performance Fix](docs/banner.jpg)

*[English version](README.md)*

Nivalis Nights peut ramer dans les endroits animés comme les marchés, et une grosse carte graphique n'y change pas
grand-chose : c'est le processeur qui limite le jeu. Ce mod BepInEx allège le travail du processeur là où ça ne se
voit pas.

Dans mes tests, un marché bondé est passé d'environ 90 à environ 118 FPS, et les saccades en ville ont presque
disparu. Les résultats dépendent de ton processeur et de l'endroit où tu es. L'affichage, le gameplay et les
sauvegardes ne changent pas.

## Ce que le mod change

- **Threads de calcul.** Règle le nombre de threads du moteur selon ton processeur. Par défaut, le jeu perd du temps
  à réveiller trop de threads.
- **Personnages éloignés.** Les gens au loin ou hors écran mettent à jour leur animation moins souvent.
- **Caméras d'arrière-plan.** Les caméras du ciel et des traces dans la neige font leur rendu une image sur
  plusieurs.
- **Emploi du temps et trajets des PNJ.** Les PNJ replanifient leur journée et relisent leur chemin moins souvent.
- **Nettoyage de la mémoire.** Le nettoyage du jeu, qui provoque une petite saccade, passe environ 4 fois moins
  souvent.
- **Tracked Quests HUD.** Si tu utilises le mod *Tracked Quests HUD* de Hvizeu, une saccade qu'il provoque quand
  aucune quête n'est épinglée est supprimée.

Chaque changement peut être désactivé dans la config.

## Installation

1. Installer [BepInEx 6 IL2CPP, build 788](https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip)
   (pas le BepInEx 5 classique) : l'extraire dans le dossier du jeu et lancer le jeu une fois.
2. Télécharger le mod sur [Nexus Mods](https://www.nexusmods.com/nivalisnights/mods/57) ou la
   [dernière version](https://github.com/hoho92/Nivalis-Performance-Fix/releases/latest) ici, et l'extraire
   dans le dossier du jeu, à côté de `Nivalis Nights.exe`.
3. Lancer le jeu, puis **le relancer une fois**. Le réglage des threads n'est lu qu'au démarrage.

Après une mise à jour du jeu ou une vérification des fichiers par Steam, le mod réapplique le réglage et redemande
une relance.

Pour vérifier que le mod tourne, la console BepInEx affiche `Nivalis Performance Fix 1.0.1: 6/6 optimizations active`.

## Configuration

`BepInEx/config/hoho92.nivalisperformancefix.cfg`, créé au premier lancement. Il contient un interrupteur général,
une section par changement, et `[JobWorkers] Mode` (`Auto`, `Manual` ou `Off` ; avec `Off`, le mod ne touche jamais
aux fichiers du jeu). Les valeurs par défaut sont celles qui ont le mieux marché.

Un conseil qui n'a rien à voir avec le mod : une souris réglée à 2000 Hz ou plus coûte des FPS dans ce jeu quand tu
tournes la caméra. 1000 Hz suffit largement.

## Si le jeu se met à jour

Si une mise à jour change le code que le mod modifie, la partie concernée se désactive toute seule et la console
indique laquelle. Le jeu continue de fonctionner normalement.

## Désinstallation

Supprimer `BepInEx/plugins/NivalisPerformanceFix`, puis lancer *Vérifier l'intégrité des fichiers du jeu* dans Steam
pour remettre le réglage d'origine des threads.

## Mises à jour

J'ai fait ce mod pour ma propre partie et je le partage au cas où il aiderait. Je le maintiens tant que je joue ;
quand j'arrêterai, les mises à jour risquent de s'arrêter aussi. Le code est sous licence MIT, donc n'importe qui peut
le reprendre.

## Pour les développeurs

Les changements viennent du profilage du jeu avec PIX et de la lecture du code désassemblé. Le mod modifie
`Character.LateUpdateAll` (postfix), `ActiveJournalEntriesUi.Refresh` (prefix, seulement avec Tracked Quests HUD) et
trois appels dans le code natif, chacun retrouvé par signature d'octets.

Les outils de développement sont désactivés par défaut (`[Developer] Enabled = true`) :

- **F8** active ou désactive tout le mod, **F9** lance un banc A/B de `BenchTarget` (rester immobile dans un endroit
  animé), **F10** mesure les temps d'image pendant 20 s.
- Un journal écrit une ligne par minute et chaque saccade dans `BepInEx/NivalisPerformanceFix.playlog.log` ;
  **F11** marque une saccade ressentie. `python tools/analyze_playlog.py` le résume.

La compilation nécessite le SDK .NET 6 et le jeu avec BepInEx installé et lancé une fois :

```
dotnet build src/NivalisPerformanceFix -c Release -p:GameDir="C:\chemin\vers\Nivalis Nights"
```

Une compilation Release copie la DLL dans le jeu (`-p:InstallToGame=false` pour l'éviter). `tools/package.ps1` crée
l'archive de publication dans `dist/`.

## Licence

[MIT](LICENSE) © hoho92
