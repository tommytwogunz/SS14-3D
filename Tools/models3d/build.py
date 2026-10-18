"""Builds the SS14 3D models. Usage: blender -b --factory-startup -P Tools/models3d/build.py -- [model ...]  (no names = all).
Writes to Resources/Models3D/. Model functions live in models_*.py and register themselves in MODELS."""
import os, sys, importlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import m3d
import models_furniture
import models_people
import models_animals

OUT = os.path.join(HERE, "..", "..", "Resources", "Models3D")
os.makedirs(OUT, exist_ok=True)

MODELS = {}
for mod in (m3d, models_furniture, models_people, models_animals):
    importlib.reload(mod)
for mod in (models_furniture, models_people, models_animals):
    MODELS.update(mod.MODELS)

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for name in args or sorted(MODELS):
    m3d.reset()
    size = MODELS[name]()
    m3d.build(name, OUT, tex_size=size or 512)
