"""Loader-owned mod provenance joins without inventing an installed pack JAR."""
import hashlib, io, json
from pathlib import Path
import tempfile, unittest, zipfile
from pack_library import read_pack_mods

def write_json(path, document):
    Path(path).write_text(json.dumps(document), encoding='utf-8')

def jar_bytes(document, nested=None):
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w') as jar:
        jar.writestr('fabric.mod.json',json.dumps(document))
        for name,raw in (nested or {}).items():jar.writestr(name,raw)
    return output.getvalue()

class RuntimeSourceProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.pack=Path(self.temp.name)
        (self.pack/'mods').mkdir();(self.pack/'library').mkdir();(self.pack/'artifacts/runtime-sources').mkdir(parents=True)
        self.runtime={'minecraftVersion':'1.6.4','loader':{'type':'legacyfabric','version':'0.18.2'}}
        write_json(self.pack/'portable-pack.json',self.runtime)
        (self.pack/'mods/base.jar').write_bytes(jar_bytes({'id':'base','name':'Base','version':'1'}))
        child=jar_bytes({'id':'mixinextras','name':'MixinExtras','version':'0.5.0'})
        self.loader=jar_bytes({'id':'fabricloader','name':'Fabric Loader','version':'0.18.2','jars':[{'file':'META-INF/jars/extra.jar'}]}, {'META-INF/jars/extra.jar':child})
        self.cache='artifacts/runtime-sources/loader.jar';(self.pack/self.cache).write_bytes(self.loader)
        self.row={'cachePath':self.cache,'sourceUrl':'https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.18.2/fabric-loader-0.18.2.jar','sizeBytes':len(self.loader),'sha256':hashlib.sha256(self.loader).hexdigest(),'modIds':['mixinextras']}
        write_json(self.pack/'library/runtime-sources.json',{'schemaVersion':1,'sources':[self.row]})

    def tearDown(self):self.temp.cleanup()

    def snapshot(self):
        return {'schemaVersion':1,**self.runtime,'sourceJars':{'mods/base.jar':hashlib.sha256((self.pack/'mods/base.jar').read_bytes()).hexdigest()},'runtimeSources':{self.cache:self.row['sha256']},
                'mods':[{'id':mid,'name':mid,'version':version} for mid,version in [('minecraft','1.6.4'),('java','17'),('fabricloader','0.18.2'),('base','1'),('mixinextras','0.5.0')]]}

    def test_actual_loader_library_is_counted_without_platform_or_extra_pack_jar(self):
        write_json(self.pack/'library/loaded-mods.json',self.snapshot())
        mods=read_pack_mods(self.pack)
        self.assertEqual({'base','mixinextras'},set(mods));self.assertEqual('0.5.0',mods['mixinextras']['version'])
        self.assertEqual(['base.jar'],[p.name for p in (self.pack/'mods').iterdir()])
        self.assertTrue((self.pack / self.cache).is_file())

    def test_actual_snapshot_cannot_omit_or_change_runtime_provenance(self):
        for expected in ({},{self.cache:'0'*64}):
            snap=self.snapshot();snap['runtimeSources']=expected;write_json(self.pack/'library/loaded-mods.json',snap)
            with self.subTest(expected=expected),self.assertRaisesRegex(ValueError,'Actual loader inventory'):read_pack_mods(self.pack)

    def test_changed_loader_bytes_and_missing_selected_id_are_rejected(self):
        (self.pack/self.cache).write_bytes(b'wrong cached loader')
        with self.assertRaisesRegex(ValueError,'runtime source JAR'):read_pack_mods(self.pack)
        (self.pack/self.cache).write_bytes(self.loader);self.row['modIds']=['invented_id']
        write_json(self.pack/'library/runtime-sources.json',{'schemaVersion':1,'sources':[self.row]})
        with self.assertRaisesRegex(ValueError,'mod ID is absent'):read_pack_mods(self.pack)

    def test_runtime_source_cannot_be_smuggled_into_game_payload_path(self):
        self.row['cachePath']='mods/loader.jar';write_json(self.pack/'library/runtime-sources.json',{'schemaVersion':1,'sources':[self.row]})
        with self.assertRaisesRegex(ValueError,'runtime source'):read_pack_mods(self.pack)

if __name__=='__main__':unittest.main()
