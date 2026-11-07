// A C++ record with an anonymous union member: the C++ record path has no representation of it.
struct fx_cpp_am
{
  int tag;
  union { int i; float f; };
};

void fx_cpp_am_read(fx_cpp_am *am);
