using RelaxKonOS.Server.Files;

var checks = 0;
void Equal(string[] expected, params string[] lines)
{
    var actual = LinuxDriveMounts.SelectPaths(lines);
    if (!expected.SequenceEqual(actual)) throw new Exception($"Expected {string.Join(",", expected)}, got {string.Join(",", actual)}");
    checks++;
}
string Mount(string device, string root, string path, string type, string source) => $"1 0 {device} {root} {path} rw - {type} {source} rw";
var root = Mount("8:1", "/", "/", "ext4", "/dev/sda1");
Equal(["/"]);
Equal(["/"], "garbage", "1 0 - ext4", root);
Equal(["/", "/mnt/data", "/media/user/USB Drive"], root,
    Mount("8:2", "/", "/mnt/data", "ext4", "/dev/sdb1"),
    Mount("8:3", "/", "/media/user/USB\\040Drive", "exfat", "/dev/sdc1"));
Equal(["/"], root, Mount("0:1", "/", "/dev/shm", "tmpfs", "tmpfs"),
    Mount("0:2", "/", "/proc", "proc", "proc"), Mount("0:3", "/", "/mnt/share", "cifs", "//host/share"),
    Mount("0:4", "/", "/mnt/nfs", "nfs", "host:/export"), Mount("0:5", "/", "/mnt/cloud", "fuse.rclone", "cloud:"));
Equal(["/"], root, Mount("7:1", "/", "/snap/tool/1", "squashfs", "/dev/loop1"),
    Mount("8:2", "/", "/etc/hosts", "ext4", "/dev/sdb"), Mount("8:3", "/", "/boot/efi", "vfat", "/dev/sda3"));
Equal(["/", "/mnt/data"], root, Mount("8:2", "/", "/mnt/data", "ext4", "/dev/sdb1"),
    Mount("8:2", "/", "/mnt/copies/data", "ext4", "/dev/sdb1"), Mount("8:1", "/", "/mnt/root-alias", "ext4", "/dev/sda1"));
Equal(["/", "/home"], root, Mount("8:1", "/home", "/home", "btrfs", "/dev/sda1"));
Equal(["/", "/run/media/user/DISK"], root, Mount("8:2", "/", "/run/media/user/DISK", "ntfs3", "/dev/sdb1"),
    Mount("8:3", "/", "/run/system/volume", "ext4", "/dev/sdc1"));
Equal(["/", "/media/CD"], root, Mount("11:0", "/", "/media/CD", "iso9660", "/dev/sr0"));
Equal(["/", "/mnt/a\\b"], root, Mount("8:2", "/", "/mnt/a\\134b", "ext4", "/dev/sdb1"));
Console.WriteLine($"PASS {checks} Linux volume selection checks");
