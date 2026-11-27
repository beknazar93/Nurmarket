import sys, pathlib
from PIL import Image
code, lang, out = sys.argv[1], sys.argv[2], sys.argv[3]
d = pathlib.Path(__file__).parent / 'screens' / code
fs = sorted(d.glob(f'{lang}_*.png'))
sc = 0.5
ims = [Image.open(f) for f in fs]
w, h = int(960*sc), int(1051*sc)
cols = 4
rows = (len(ims)+cols-1)//cols
sh = Image.new('RGB', (cols*w, rows*h), 'white')
for i, im in enumerate(ims):
    sh.paste(im.resize((w, h)), ((i%cols)*w, (i//cols)*h))
sh.save(out)
print([f.name for f in fs])
