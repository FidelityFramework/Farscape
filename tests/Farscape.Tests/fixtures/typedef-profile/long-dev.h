/* Issue #54 fixture: this unit declares dev_t as unsigned long, agreeing with the LP64 profile row. */
typedef unsigned long __fx_dev_t;
typedef __fx_dev_t dev_t;
struct fx_rec {
    dev_t device;
    unsigned short flags;
    unsigned int pad[2];
};
