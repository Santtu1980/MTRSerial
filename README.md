# MTRSerial
Net Standard library for Emit MTR reader  
This is done for the Emit card 250 reader.  
The original protocol:  
Type: Regnly EPT system  
Data from 250 reader.  
Communication settings: RS323, 9600, No parity, 8 bit, 2 stop bit.

Byte | Description | Bytes
-----|-------------|------
1 | Package start Identification FFH | 1
2 | Package start Identification FFH | 1
3 | E-cards no LSB Binary code Max 999999 DEC | 1
4 | E-cards no LSB Binary code Max 999999 DEC | 1
5 | E-cards no LSB Binary code Max 999999 DEC | 1
6 | not used | 1
7 | Production week Binary 1-53 | 1
8 | Production year Binary 94-xx | 1
9 | not used | 1
10 | check byte e-card no. Sum of bytes 3-10 = Bin 0 (Mod 256) | 1
11-160 | Control codes and times. 50x3bytes = 150 bytes | 
(1) | byte binary control code 0-250 | 1
(2) | bytes binary time 0-65534 sec. 150 | 2
161-168 | Ascii string Emit time system/ Runners name | 8
169-174 | Ascii string Emit time system/ Runners name | 8
177-184 | Ascii string Emit time system/ Runners name | 8
185-192 | Ascii string Emit time system/ Runners name | 8
193-200 | Ascii string Disp 1 | 8
201-208 | Ascii string Disp 2 | 8
209-216 | Ascii string Disp 3 | 8
217 | check byte. Sum of all bytes 1-217 = bin 0 (Mod 256) | 1
-------------
Sum 217  
All info must be xor with **0F** before seperated  
Disp 2-3 is now used for counters:  
Disp 2:S0000P00 S0000 -> Numbers of disturbance  
Disp3:00L00000 P0000 -> Numbers of tests  
L0000 -> Numbers of races
