# libc Library Standardization

## Context

Farscape generates `[<FidelityExtern>]` bindings from C headers via Moya-scoped generation. This document defines the standard libc library decomposition for the Fidelity ecosystem: which functions belong in which libraries, what is excluded (already native), and how multi-header generation should work.

## The libc Surface

libc is the kernel interface abstraction. If something is a syscall, libc wraps it. Anything requiring a userspace driver, daemon, or protocol stack is a separate library (libusb, libcups, GTK, ALSA, etc.).

libc functions fall into two categories:

1. **Already lowered natively** by MLIR/FNCS (exclude from generation)
2. **OS interaction** that cannot be expressed as MLIR ops (generate bindings)

### Excluded: Already Native

| Category | Examples | Native path |
|----------|----------|-------------|
| Memory allocation | malloc, free, calloc, realloc | `memref.alloc` / `memref.dealloc` via MLIR lowering |
| String operations | strlen, strcmp, strcpy, memcpy, memset | FNCS string intrinsics |
| Math | sin, cos, sqrt, pow, fabs | LLVM math intrinsics |
| Conversion | atoi, strtol, strtod | Fidelity.Platform Format/Parse modules |

These are handled at the MLIR or FNCS level and do not need Farscape bindings. Generating them would create redundant, competing paths.

### Included: OS Interaction

These are the functions that require actual kernel interaction and cannot be synthesized from MLIR operations.

## Standard Library Decomposition

### Fidelity.libc.IO

**Header:** `unistd.h`

Core POSIX I/O. File descriptors, not streams.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `read` | `ssize_t read(int fd, void *buf, size_t count)` | Read bytes from fd |
| `write` | `ssize_t write(int fd, const void *buf, size_t count)` | Write bytes to fd |
| `open` | `int open(const char *path, int flags, mode_t mode)` | Open file, return fd |
| `close` | `int close(int fd)` | Close fd |
| `lseek` | `off_t lseek(int fd, off_t offset, int whence)` | Seek within fd |
| `stat` | `int stat(const char *path, struct stat *buf)` | File metadata |
| `fstat` | `int fstat(int fd, struct stat *buf)` | File metadata by fd |
| `dup` | `int dup(int fd)` | Duplicate fd |
| `dup2` | `int dup2(int fd, int fd2)` | Duplicate fd to specific number |
| `pipe` | `int pipe(int pipefd[2])` | Create pipe |
| `_exit` | `void _exit(int status)` | Immediate process termination |

**Useful macros:** `STDIN_FILENO`, `STDOUT_FILENO`, `STDERR_FILENO`, `SEEK_SET`, `SEEK_CUR`, `SEEK_END`

Note: `open` is declared in `fcntl.h`, not `unistd.h`. See Multi-Header Generation below.

### Fidelity.libc.FileSystem

**Headers:** `dirent.h`, `sys/stat.h`, `unistd.h`

Directory traversal and filesystem manipulation.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `opendir` | `DIR *opendir(const char *name)` | Open directory stream |
| `readdir` | `struct dirent *readdir(DIR *dirp)` | Read next directory entry |
| `closedir` | `int closedir(DIR *dirp)` | Close directory stream |
| `mkdir` | `int mkdir(const char *path, mode_t mode)` | Create directory |
| `rmdir` | `int rmdir(const char *path)` | Remove empty directory |
| `rename` | `int rename(const char *old, const char *new)` | Rename file or directory |
| `unlink` | `int unlink(const char *path)` | Delete file |
| `getcwd` | `char *getcwd(char *buf, size_t size)` | Get working directory |
| `chdir` | `int chdir(const char *path)` | Change working directory |
| `chmod` | `int chmod(const char *path, mode_t mode)` | Change file permissions |

**Struct dependencies:** `struct dirent`, `struct stat` (passed through by Moya filtering)

### Fidelity.libc.Process

**Headers:** `stdlib.h`, `unistd.h`, `sys/wait.h`

Process lifecycle and environment.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `exit` | `void exit(int status)` | Clean process termination |
| `getenv` | `char *getenv(const char *name)` | Read environment variable |
| `setenv` | `int setenv(const char *name, const char *value, int overwrite)` | Set environment variable |
| `getpid` | `pid_t getpid(void)` | Current process ID |
| `fork` | `pid_t fork(void)` | Fork process |
| `execve` | `int execve(const char *path, char *const argv[], char *const envp[])` | Replace process image |
| `waitpid` | `pid_t waitpid(pid_t pid, int *status, int options)` | Wait for child process |
| `kill` | `int kill(pid_t pid, int sig)` | Send signal to process |

**Useful macros:** `EXIT_SUCCESS`, `EXIT_FAILURE`

### Fidelity.libc.Signal

**Header:** `signal.h`

Signal handling.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `signal` | `sighandler_t signal(int signum, sighandler_t handler)` | Set signal handler (simple) |
| `sigaction` | `int sigaction(int signum, const struct sigaction *act, struct sigaction *oldact)` | Set signal handler (full) |
| `raise` | `int raise(int sig)` | Send signal to self |
| `sigprocmask` | `int sigprocmask(int how, const sigset_t *set, sigset_t *oldset)` | Block/unblock signals |

**Struct dependencies:** `struct sigaction`, `sigset_t`

### Fidelity.libc.Time

**Headers:** `time.h`, `sys/time.h`

Time measurement and delays.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `time` | `time_t time(time_t *tloc)` | Seconds since epoch |
| `clock_gettime` | `int clock_gettime(clockid_t clk_id, struct timespec *tp)` | High-resolution time |
| `nanosleep` | `int nanosleep(const struct timespec *req, struct timespec *rem)` | Sleep with nanosecond precision |
| `gettimeofday` | `int gettimeofday(struct timeval *tv, struct timezone *tz)` | Time with microsecond precision |

**Struct dependencies:** `struct timespec`, `struct timeval`

### Fidelity.libc.Net

**Headers:** `sys/socket.h`, `netinet/in.h`, `arpa/inet.h`, `netdb.h`

BSD socket layer.

| Function | Signature | Purpose |
|----------|-----------|---------|
| `socket` | `int socket(int domain, int type, int protocol)` | Create socket |
| `bind` | `int bind(int sockfd, const struct sockaddr *addr, socklen_t addrlen)` | Bind to address |
| `listen` | `int listen(int sockfd, int backlog)` | Listen for connections |
| `accept` | `int accept(int sockfd, struct sockaddr *addr, socklen_t *addrlen)` | Accept connection |
| `connect` | `int connect(int sockfd, const struct sockaddr *addr, socklen_t addrlen)` | Connect to remote |
| `send` | `ssize_t send(int sockfd, const void *buf, size_t len, int flags)` | Send data |
| `recv` | `ssize_t recv(int sockfd, void *buf, size_t len, int flags)` | Receive data |
| `setsockopt` | `int setsockopt(int sockfd, int level, int optname, const void *optval, socklen_t optlen)` | Set socket option |
| `getaddrinfo` | `int getaddrinfo(const char *node, const char *service, const struct addrinfo *hints, struct addrinfo **res)` | DNS resolution |
| `freeaddrinfo` | `void freeaddrinfo(struct addrinfo *res)` | Free DNS result |

**Struct dependencies:** `struct sockaddr`, `struct sockaddr_in`, `struct addrinfo`

## What About stdio Streams?

libc has two I/O layers:

- **Low-level** (`read`/`write`/`open`/`close`): maps 1:1 to syscalls, unbuffered, file-descriptor-based. This is what `Fidelity.libc.IO` targets.

- **stdio** (`fopen`/`fclose`/`fread`/`fwrite`/`fprintf`/`fgets`): a pure C library construct that wraps file descriptors with userspace buffering. `FILE*` is an opaque struct containing an internal buffer, flush policy, and text-mode translation state. `fprintf` does not become a syscall; it formats into a buffer, and eventually that buffer is flushed via the low-level `write`.

The stdio layer is not a good binding target for Fidelity because:

1. **Opaque state**: `FILE*` internal structure is implementation-defined and varies across libc implementations (glibc vs musl vs macOS libSystem)
2. **Redundant buffering**: Fidelity's string and I/O infrastructure already handles buffering at the MLIR level
3. **Format strings**: `printf`-family functions use C varargs, which do not map cleanly to F# calling conventions

The low-level I/O layer provides everything stdio does, minus the buffering (which Fidelity can implement natively) and minus varargs formatting (which Fidelity's Format module handles).

## Platform Binding Model

Each library exists in the context of a platform triple:

### Console Mode (dynamically linked to libc)

The library name varies by OS:

| Platform | Library | Package |
|----------|---------|---------|
| Linux (any arch) | `libc` (libc.so) | `Fidelity.libc.*` |
| macOS (any arch) | `libSystem` (libSystem.B.dylib) | `Fidelity.libSystem.*` |
| Windows (any arch) | `ucrt` (ucrt.dll) | `Fidelity.ucrt.*` |

Function signatures are identical across Linux architectures for libc. macOS is mostly POSIX-compatible. Windows has some name differences (`_read`, `_write`, `_open` with underscore prefixes).

The fidproj `output_kind = "console"` selects this path.

### Freestanding Mode (raw syscalls)

A parallel `Fidelity.Syscall.*` library set provides the same functional shape but implemented as inline syscall sequences. These are inherently platform-specific:

| Variant | write syscall | Instruction | Arg registers |
|---------|--------------|-------------|---------------|
| Linux_x86_64 | 1 | `syscall` | rdi, rsi, rdx |
| Linux_ARM64 | 64 | `svc #0` | x0, x1, x2 |
| Linux_RISCV64 | 64 | `ecall` | a0, a1, a2 |
| Linux_ARM32 | 4 | `swi #0` | r0, r1, r2 |

Syscall numbers and calling conventions are stable within an architecture (no kernel version or distribution variance). The variant dimension is the target triple.

The fidproj `output_kind = "freestanding"` selects this path.

### Dependency Model

```
Fidelity.Platform/{triple}/Console.fs
  [console]      --> Fidelity.libc.IO        (platform-agnostic for a given OS)
  [freestanding]  --> Fidelity.Syscall.IO/{triple}  (architecture-specific)
```

The developer writes `Console.write "hello"` and never sees the binding layer. The fidproj triple determines which dependency is live.

## Multi-Header Generation

### The Problem

Moya's current `[library]` section has a single `header` field. But logical library decompositions frequently span multiple C headers:

| Library | Headers needed |
|---------|---------------|
| Fidelity.libc.IO | `unistd.h`, `fcntl.h` (for `open`) |
| Fidelity.libc.FileSystem | `dirent.h`, `sys/stat.h`, `unistd.h` |
| Fidelity.libc.Process | `stdlib.h`, `unistd.h`, `sys/wait.h` |
| Fidelity.libc.Signal | `signal.h` |
| Fidelity.libc.Time | `time.h`, `sys/time.h` |
| Fidelity.libc.Net | `sys/socket.h`, `netinet/in.h`, `arpa/inet.h`, `netdb.h` |

Only `Fidelity.libc.Signal` maps to a single header. Every other library draws from 2-4 headers.

### Current Workaround

Use one `.moya.toml` file per header, with explicit `functions` lists scoping to only the needed functions. This works but requires multiple generation passes and produces separate output files that must be manually combined.

### Proposed Enhancement: Multi-Header Moya

Extend `[library]` to accept a header list:

```toml
[library]
name = "libc"
headers = [
    "/usr/include/unistd.h",
    "/usr/include/fcntl.h"
]
```

Farscape would run clang's two-pass extraction (JSON AST + macros) against each header, merge the declaration lists, deduplicate shared typedefs, and then apply Moya's namespace filtering to the merged set.

This is a natural evolution: the clang parsing and Moya filtering stages are already independent. The only new work is the merge-and-deduplicate step between them. The single dual-pass process handles all headers, and Moya's `[[namespace]]` sections slice the merged declarations into the desired library exports.

```
Multiple C Headers
    |
    v  (clang dual-pass per header)
Merged Declaration List
    |
    v  (Moya namespace filtering)
Fidelity.libc.IO        -- read, write, open, close, lseek, stat
Fidelity.libc.FileSystem -- opendir, readdir, mkdir, rmdir, rename
Fidelity.libc.Process    -- exit, fork, exec, waitpid, getenv
Fidelity.libc.Signal     -- signal, sigaction, raise
Fidelity.libc.Time       -- clock_gettime, nanosleep, time
Fidelity.libc.Net        -- socket, bind, listen, accept, send, recv
```

One moya.toml, one `farscape project` invocation, six library outputs. This is the forcing function: libc standardization requires multi-header support to be practical, and multi-header support makes all future library generation (GTK, SDL, OpenSSL, etc.) significantly cleaner.

## Generation Recipes

### Minimal Console (implemented now)

Two separate moya.toml files, one per header:

```
Farscape_samples/
  Fidelity.libc.IO/
    unistd.moya.toml    # functions = ["read", "write", "_exit"]
    IO.fs                # 3 FidelityExtern stubs + macros
  Fidelity.libc.Process/
    stdlib.moya.toml     # functions = ["exit"]
    Process.fs           # 1 FidelityExtern stub + macros
```

### Full libc (requires multi-header enhancement)

Single moya.toml with merged headers:

```toml
[library]
name = "libc"
headers = [
    "/usr/include/unistd.h",
    "/usr/include/fcntl.h",
    "/usr/include/dirent.h",
    "/usr/include/sys/stat.h",
    "/usr/include/sys/wait.h",
    "/usr/include/signal.h",
    "/usr/include/time.h",
    "/usr/include/sys/time.h",
    "/usr/include/sys/socket.h",
    "/usr/include/netinet/in.h",
    "/usr/include/arpa/inet.h",
    "/usr/include/netdb.h",
    "/usr/include/stdlib.h"
]
defines = ["_GNU_SOURCE"]

[output]
mode = "fidelity"
directory = "./bindings"

[[namespace]]
name = "Fidelity.libc.IO"
description = "POSIX low-level I/O"
library = "libc"
prefixes = []
functions = ["read", "write", "open", "close", "lseek", "stat", "fstat",
             "dup", "dup2", "pipe", "_exit"]

[[namespace]]
name = "Fidelity.libc.FileSystem"
description = "Directory traversal and filesystem manipulation"
library = "libc"
prefixes = []
functions = ["opendir", "readdir", "closedir", "mkdir", "rmdir",
             "rename", "unlink", "getcwd", "chdir", "chmod"]

[[namespace]]
name = "Fidelity.libc.Process"
description = "Process lifecycle and environment"
library = "libc"
prefixes = []
functions = ["exit", "getenv", "setenv", "getpid", "fork",
             "execve", "waitpid", "kill"]

[[namespace]]
name = "Fidelity.libc.Signal"
description = "Signal handling"
library = "libc"
prefixes = []
functions = ["signal", "sigaction", "raise", "sigprocmask"]

[[namespace]]
name = "Fidelity.libc.Time"
description = "Time measurement and delays"
library = "libc"
prefixes = []
functions = ["time", "clock_gettime", "nanosleep", "gettimeofday"]

[[namespace]]
name = "Fidelity.libc.Net"
description = "BSD socket layer"
library = "libc"
prefixes = []
functions = ["socket", "bind", "listen", "accept", "connect",
             "send", "recv", "setsockopt", "getaddrinfo", "freeaddrinfo"]
```
