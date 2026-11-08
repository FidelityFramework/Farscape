/* BAREWire #11: a flexible array member whose declared count field is no integer. */
struct fx_float_count {
    double length;
    unsigned char data[];
};
int fx_float_count_use(struct fx_float_count *value);
