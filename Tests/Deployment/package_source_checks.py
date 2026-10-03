"""Test the Linux launcher's actual helper without running any service installer."""
import errno, hashlib, json, tempfile, unittest, zipfile
from unittest.mock import patch
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
    def test_system_package_requires_uninstall_engine(self):
        self.manifest['packageKind'] = 'server'
        self.files.update({
            'payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper': b'helper',
            'deployment/bootstrap/install-relaxkonos.sh': b'#!/bin/bash\n',
            'deployment/linux/install-relaxkonos-services.sh': b'#!/bin/bash\n',
        })
        self.package()
        with self.assertRaisesRegex(ValueError, 'incomplete package'):
            helper['extract']('localBundle', 'linux-x64', 'server', self.root/'missing', str(self.archive))
        self.assertFalse((self.root/'missing').exists())
        self.files['deployment/bootstrap/uninstall-relaxkonos.sh'] = b'#!/bin/bash\n'
        self.package()
        helper['extract']('localBundle', 'linux-x64', 'server', self.root/'complete', str(self.archive))
        self.assertTrue((self.root/'complete/deployment/bootstrap/uninstall-relaxkonos.sh').is_file())

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
    def test_custom_https_digest(self):
        self.package()
        options={'source':'directUrl','network':'loopback','packageUri':'https://example.invalid/release.zip',
                 'packageDigest':hashlib.sha256(self.archive.read_bytes()).hexdigest()}
        path=self.root/'request.json'
        def write(): path.write_text(json.dumps({'schemaVersion':1,'operationId':'12345678-1234-1234-1234-123456789abc','kind':'install','options':options}),encoding='utf-8')
        write()
        original=helper['download']
        helper['download']=lambda url,target,limit: Path(target).write_bytes(self.archive.read_bytes())
        try:
            helper['extract']('directUrl','linux-x64','user-server',str(self.root/'custom'),'',str(path))
            self.assertTrue((self.root/'custom/manifest.json').is_file())
            options['packageDigest']='0'*64;write()
            with self.assertRaises(ValueError):helper['extract']('directUrl','linux-x64','user-server',str(self.root/'bad-custom'),'',str(path))
            self.assertFalse((self.root/'bad-custom').exists())
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

    def test_full_partition_is_rejected_before_extraction(self):
        self.package()
        with patch.object(helper['shutil'], 'disk_usage', return_value=type('Usage', (), {'free':0})()):
            with self.assertRaises(OSError) as caught:self.extract()
        self.assertEqual(errno.ENOSPC, caught.exception.errno)
        self.assertFalse((self.root/'out').exists())

    def test_quota_failure_removes_partial_extraction(self):
        self.package()
        with patch.object(helper['shutil'], 'copyfileobj', side_effect=OSError(errno.EDQUOT, 'quota exceeded')):
            with self.assertRaises(OSError) as caught:self.extract()
        self.assertEqual(errno.EDQUOT, caught.exception.errno)
        self.assertFalse((self.root/'out').exists())

if __name__=='__main__':unittest.main()
