import json,pathlib
p=pathlib.Path(__file__).with_name('mixed-stress.json')
d=json.loads(p.read_text())
d['code']=d['code'].replace('i<100','i<25').replace('completed==100','completed==25').replace('100 seeded mixed moves','25 seeded mixed moves')
d['code']=d['code'].replace('return string.Join("\\n",results);','System.IO.File.WriteAllText("Assets/EasyExpressionManager/Tests/Stress/mixed-results.txt",string.Join("\\n",results));return string.Join("\\n",results);')
p.with_name('mixed-short.json').write_text(json.dumps(d))
