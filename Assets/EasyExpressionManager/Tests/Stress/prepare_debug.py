import pathlib,json
base=json.loads(pathlib.Path(__file__).with_name('native-stress.json').read_text())['code']
prefix=base[:base.index('try {\nvar root')]
suffix=base[base.index('} catch(System.Exception ex)'):]
body=r'''
try {
var settings=(System.Collections.Generic.Dictionary<string,object>)invoke(w,"LoadDeveloperSettings",null);settings["DebugJSON"]=false;
var root=menu("DebugOff");root.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control{name="Original",type=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle});da.expressionsMenu=root;invoke(w,"Scan",null);
var rows=(System.Collections.Generic.List<ExpressionNode>)type.GetField("allNodesFlatList",flags).GetValue(w);invoke(w,"DoRename",new object[]{rows[0],"Renamed"});
var entries=(System.Collections.ICollection)type.GetField("debugLog",flags).GetValue(w);check("DebugJSON false records no verification entries",entries.Count==0,"debug entries="+entries.Count);check("DebugJSON false edit persists",da.expressionsMenu.controls[0].name=="Renamed","name="+da.expressionsMenu.controls[0].name);
var state=(EEMAvatarState)invoke(w,"AvatarState",null);AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(state));
'''
pathlib.Path(__file__).with_name('debug-off.json').write_text(json.dumps({'action':'execute','code':prefix+body+suffix,'safety_checks':False}))
