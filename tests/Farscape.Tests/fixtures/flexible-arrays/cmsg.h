/* BAREWire #11: C flexible array members (C11 6.7.2.1p18). Each record's last field has no
   element count; its count is a run-time fact a pilot declares in options.flexible_arrays. */

/* glibc's struct cmsghdr shape (bits/socket.h:283-295): a length counting the header and the
   data, two ints, and an unsigned char flexible array member at the header's end. */
struct fx_cmsghdr {
    unsigned long cmsg_len;
    int cmsg_level;
    int cmsg_type;
    unsigned char cmsg_data[];
};
int fx_cmsg_use(struct fx_cmsghdr *header);

/* A flexible array member placed before the rounded size: 8 bytes at alignment 4, data at 5. */
struct fx_short {
    int count;
    char tag;
    char data[];
};
int fx_short_use(struct fx_short *value);

/* The element alignment joins the record's: 4 bytes at alignment 4, data at 4. */
struct fx_wide {
    unsigned char count;
    unsigned int data[];
};
int fx_wide_use(struct fx_wide *value);
