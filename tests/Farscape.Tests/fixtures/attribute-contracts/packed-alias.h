/* may_alias together with packed: the record's layout without may_alias is a packed layout,
   which its declared fields under natural alignment cannot give. */
struct __attribute__((__may_alias__, __packed__)) fx_packed_addr
{
  char tag;
  int value;
};

void fx_packed_addr_use(struct fx_packed_addr *addr);
