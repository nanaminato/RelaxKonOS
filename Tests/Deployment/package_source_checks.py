"""Test the Linux launcher's actual helper without running any service installer."""
import hashlib, json, tempfile, unittest, zipfile
from pathlib import Path

script = (Path(__file__).resolve().parents[2]/'deployment/launcher/relaxkonos-deploy.sh').read_text(encoding='utf-8')
code = script.split("<<'PY'\n",1)[1].split('\nPY\n',1)[0].split('\ntry:\n    action,',1)[0]
helper = {}
exec(compile(code,'linux-launcher-helper','exec'),helper)

class PackageSourceChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
        self.archive = self.root/'server.zip'
        self.files = {'payload/linux/server/RelaxKonOS.Server':b'server',
                      'payload/linux/guardian/RelaxKonOS.Guardian.Agent':b'guardian',
                      'deployment/user/relaxkon':b'#!/bin/bash\n'}
        self.manifest = {'schemaVersion':1,'packageKind':'user-server','runtime':'linux-x64','version':'0.1.0',
                         'files':[{'path':p,'length':len(v),'sha256':hashlib.sha256(v).hexdigest()} for p,v in self.files.items()]}
    def tearDown(self): self.temp.cleanup()
    def package(self,extra=None):
        with zipfile.ZipFile(self.archive,'w') as archive:
            archive.writestr('manifest.json',json.dumps(self.manifest))
            for path,data in (self.files | (extra or {})).items(): archive.writestr(path,data)
    def extract(self,source='localBundle',runtime='linux-x64',target='out'):
        helper['extract'](source,runtime,'user-server',self.root/target,str(self.archive))
    def test_user_sources_require_no_digest(self):
        self.manifest['files'][0]['sha256']='0'*64; self.package()
        for source in ('localBundle','remoteBundle'):
            self.extract(source,target=source)
            self.assertEqual((self.root/source/next(iter(self.files))).read_bytes(),b'server')
    def test_wrong_architecture(self):
        self.package()
        with self.assertRaises(ValueError): self.extract(runtime='linux-arm64')
        self.assertFalse((self.root/'out').exists())
    def test_traversal(self):
        self.package({'../escaped':b'bad'})
        with self.assertRaises(ValueError): self.extract()
        self.assertFalse((self.root/'escaped').exists())
    def test_symlink(self):
        self.package()
        with zipfile.ZipFile(self.archive,'a') as archive:
            link=zipfile.ZipInfo('link');link.external_attr=0o120777 << 16;archive.writestr(link,b'/etc/passwd')
        with self.assertRaises(ValueError): self.extract()
    def test_incomplete(self):
        del self.files['payload/linux/guardian/RelaxKonOS.Guardian.Agent'];self.package()
        with self.assertRaises(ValueError):self.extract()
    def test_official_checks(self):
        self.package(); seen=[]
        descriptor={'schemaVersion':1,'packageKind':'user-server','runtime':'linux-x64','version':'0.1.0',
                    'url':'https://example.invalid/server.zip','sha256':hashlib.sha256(self.archive.read_bytes()).hexdigest()}
        original=helper['download']
        def download(url,path,limit):
            seen.append(url);Path(path).write_bytes(json.dumps(descriptor).encode() if url.endswith('.json') else self.archive.read_bytes())
        helper['download']=download
        try:
            self.extract('officialStable');self.assertIn('/latest/user-server/linux-x64.json',seen[0])
            descriptor['sha256']='0'*64
            with self.assertRaises(ValueError):self.extract('officialStable',target='bad-zip')
            self.manifest['files'][0]['sha256']='0'*64;self.package()
            descriptor['sha256']=hashlib.sha256(self.archive.read_bytes()).hexdigest()
            with self.assertRaises(ValueError):self.extract('officialStable',target='bad-file')
        finally:helper['download']=original
    def test_strict_json(self):
        value={'schemaVersion':1,'operationId':'12345678-1234-1234-1234-123456789abc','kind':'install',
               'options':{'source':'remoteBundle','network':'loopback','remotePackagePath':'/home/a/服务器包.zip'}}
        path=self.root/'request.json';path.write_text(json.dumps(value,ensure_ascii=False),encoding='utf-8')
        self.assertEqual(helper['request'](path)['options']['remotePackagePath'],value['options']['remotePackagePath'])
        for raw in [json.dumps(value).replace('"schemaVersion": 1','"schemaVersion": 1, "schemaVersion": 1'),
                    json.dumps(value | {'command':'rm'}),json.dumps(value | {'options':value['options'] | {'confirmed':'true'}})]:
            path.write_text(raw)
            with self.assertRaises(ValueError):helper['request'](path)

if __name__=='__main__':unittest.main()
