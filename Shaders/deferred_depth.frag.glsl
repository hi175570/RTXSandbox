#version 430 core

flat in float vTransmission;

void main()
{
    if (vTransmission > 0.0) discard;
}
