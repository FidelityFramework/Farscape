/* A may_alias record (glibc's struct sockaddr shape, bits/socket.h:184) and the same record
   declared without the attribute: clang lays both out alike, since may_alias moves no byte. */
struct __attribute__((__may_alias__)) fx_addr
{
  unsigned short family;
  char data[14];
};

struct fx_addr_plain
{
  unsigned short family;
  char data[14];
};

void fx_addr_use(struct fx_addr *addr);
