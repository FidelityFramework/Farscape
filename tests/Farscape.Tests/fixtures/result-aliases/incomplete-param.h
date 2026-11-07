/* A legal declaration taking an incomplete record by value: no call of it can be typed, so
   its result alias has no readable canonical type. */
struct fx_opaque;
__int128_t fx_widen(struct fx_opaque value);
