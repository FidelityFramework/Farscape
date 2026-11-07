/* Issue #54 fixture: this unit declares dev_t as unsigned short (16 bits) through a chain;
   the LP64 profile row says unsigned 64-bit. Record size 12 admits the wrong 8-byte extent. */
typedef unsigned short __fx_dev_t;
typedef __fx_dev_t dev_t;
struct fx_rec {
    dev_t device;
    unsigned short flags;
    unsigned int pad[2];
};
