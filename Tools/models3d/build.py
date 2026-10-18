"""Builds the SS14 3D models. Usage: blender -b --factory-startup -P Tools/models3d/build.py -- [model ...]  (no names = all).
Writes to ss14/Resources/Models3D/. Model functions live in models_*.py and register themselves in MODELS."""
import os, sys, importlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import m3d
import models_furniture
importlib.reload(m3d)
importlib.reload(models_furniture)

OUT = os.path.join(HERE, "..", "..", "Resources", "Models3D")
os.makedirs(OUT, exist_ok=True)

MODELS = {}
MODELS.update(models_furniture.MODELS)
try:
    import models_people
    importlib.reload(models_people)
    MODELS.update(models_people.MODELS)
except ImportError:
    pass

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for name in args or sorted(MODELS):
    m3d.reset()
    size = MODELS[name]()
    m3d.build(name, OUT, tex_size=size or 512)
