#ifndef FARSCAPE_BOUNDARY_OBJECT_CONFORMANCE_HPP
#define FARSCAPE_BOUNDARY_OBJECT_CONFORMANCE_HPP

class BoundaryWidget {
public:
    explicit BoundaryWidget(int value);
    ~BoundaryWidget();
    int read() const;
private:
    int value;
};

#endif
