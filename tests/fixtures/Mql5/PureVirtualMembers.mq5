// MQL5 fixture: Issue #143 — pure virtual function specifiers
// Documented spellings (MQL5 reference: docs/basis/oop/abstract_type; the
// compiler accepts exactly "= 0" and "= NULL" — compile error 381).
// Also pins `override` as a member modifier (docs/basis/syntax/reserved).
abstract class CAnimal {
public:
    virtual void Sound() = 0;
private:
    double m_legs_count;
};

abstract class CBird {
public:
    virtual void Fly() = NULL;   // documented "= NULL" spelling
};

class CCat : public CAnimal {
public:
    virtual void Sound() { }     // defined override — regression guard
};

class CDog : public CAnimal {
public:
    virtual void Sound();        // declared, not pure — regression guard
    void Run() override { }      // documented override modifier
};

void OnStart() {
    CCat cat;
    CDog dog;
}
