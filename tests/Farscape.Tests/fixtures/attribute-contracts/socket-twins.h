/* glibc's struct sockaddr and struct sockaddr_storage (bits/socket.h:184 and :197), declared
   again with the same members and without __attribute_struct_may_alias__. */
#include <sys/socket.h>

struct fx_sockaddr_plain
{
  __SOCKADDR_COMMON (sa_);
  char sa_data[14];
};

struct fx_sockaddr_storage_plain
{
  __SOCKADDR_COMMON (ss_);
  char __ss_padding[_SS_PADSIZE];
  __ss_aligntype __ss_align;
};
