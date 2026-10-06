"""Game folder for the tools: NIVALIS_GAME_DIR, else GameDir in GameDir.props at the repository root (local, not in git)."""
import os
import re
import sys

PROPS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'GameDir.props')


def game_dir():
    path = os.environ.get('NIVALIS_GAME_DIR')
    if not path and os.path.exists(PROPS):
        with open(PROPS, encoding='utf-8') as f:
            m = re.search(r'<GameDir[^>]*>([^<]+)</GameDir>', f.read())
        path = m and m.group(1).strip()
    if not path:
        sys.exit('Game folder unknown: set NIVALIS_GAME_DIR, create GameDir.props (see README, Building) or pass a path.')
    return path
