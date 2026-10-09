import json,pathlib
p=pathlib.Path(__file__).with_name('mixed-stress.json')
d=json.loads(p.read_text())
start=d['code'].index('var ft=(System.Type)invoke(w,"FindFeatureType",new object[]{"FullController"});')
end=d['code'].index('} catch(System.Exception ex)')
d['code']=d['code'][:start]+r'''
invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);
var beforeToggles=rows.FindAll(n=>!n.IsFolderLike).Count;
var folderNames=new[]{"F0","F1","F2","F3","Copy"};int dissolved=0;
foreach(var name in folderNames){invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);var folder=rows.Find(n=>n.rawName==name && n.IsFolderLike);if(folder==null)continue;Undo.IncrementCurrentGroup();bool accepted=(bool)invoke(w,"DissolveFolder",new object[]{folder});invoke(w,"Scan",null);rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);check("Dissolve after long move history "+name,accepted && !rows.Exists(n=>n.rawName==name && n.IsFolderLike) && rows.FindAll(n=>!n.IsFolderLike).Count==beforeToggles,"accepted="+accepted+" toggles="+rows.FindAll(n=>!n.IsFolderLike).Count+" expected="+beforeToggles);if(accepted)dissolved++;}
var snapshots=(string)invoke(w,"SnapshotFolder",null);AssetDatabase.DeleteAsset(snapshots);var state=(EEMAvatarState)invoke(w,"AvatarState",null);AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(state));
'''+d['code'][end:]
d['code']=d['code'].replace('completed++;','completed++;if((i+1)%10==0){results.Add("MEASURE moves="+(i+1)+" cumulative_ms="+timer.ElapsedMilliseconds);System.IO.File.WriteAllText("Assets/EasyExpressionManager/Tests/Stress/ramp-results.txt",string.Join("\\n",results));}')
d['code']=d['code'].replace('return string.Join("\\n",results);','System.IO.File.WriteAllText("Assets/EasyExpressionManager/Tests/Stress/ramp-results.txt",string.Join("\\n",results));return string.Join("\\n",results);')
p.with_name('ramp-stress.json').write_text(json.dumps(d))
