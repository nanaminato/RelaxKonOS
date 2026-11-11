/* Linux acceptance fixture. Pause only the new Compose association's ledger rename. */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static int count_receipts(const char *path)
{
    static const char key[] = "\"receiptId\":\"";
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0)
        return -1;
    struct stat stat;
    if (fstat(fd, &stat) != 0 || !S_ISREG(stat.st_mode) || stat.st_size <= 0 || stat.st_size > 16 * 1024 * 1024) {
        close(fd);
        return -1;
    }
    size_t length = (size_t)stat.st_size;
    const char *data = mmap(NULL, length, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (data == MAP_FAILED)
        return -1;
    int count = 0;
    const char *cursor = data;
    const char *end = data + length;
    const char *match;
    while ((match = memmem(cursor, (size_t)(end - cursor), key, sizeof(key) - 1)) != NULL) {
        count++;
        cursor = match + sizeof(key) - 1;
    }
    munmap((void *)data, length);
    return count;
}

int rename(const char *source, const char *destination)
{
    int (*next)(const char *, const char *) = dlsym(RTLD_NEXT, "rename");
    if (next == NULL) {
        errno = ENOSYS;
        return -1;
    }
    const char *ledger = getenv("RK_CA_HOLD_LEDGER");
    const char *marker = getenv("RK_CA_HOLD_MARKER");
    const char *armed = getenv("RK_CA_HOLD_ARMED");
    if (ledger == NULL || marker == NULL || armed == NULL || ledger[0] != '/' || marker[0] != '/' || armed[0] != '/'
        || strcmp(destination, ledger) != 0 || access(armed, F_OK) != 0)
        return next(source, destination);
    size_t ledger_length = strlen(ledger);
    if (strncmp(source, ledger, ledger_length) != 0 || strcmp(source + ledger_length, ".tmp") != 0)
        return next(source, destination);
    int previous = count_receipts(destination);
    if (previous < 0 || count_receipts(source) != previous + 1)
        return next(source, destination);

    int fd = open(marker, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
    if (fd < 0)
        return -1;
    int recorded = dprintf(fd, "%ld\n", (long)getpid());
    close(fd);
    if (recorded < 0) {
        errno = EIO;
        return -1;
    }
    const struct timespec interval = { .tv_nsec = 100000000 };
    for (int attempt = 0; attempt < 1800; attempt++) {
        if (access(armed, F_OK) != 0)
            return next(source, destination);
        nanosleep(&interval, NULL);
    }
    errno = ETIMEDOUT;
    return -1;
}
