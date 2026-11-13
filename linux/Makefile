obj-m += hid-hp-zbook-kbd.o

KDIR ?= /lib/modules/$(shell uname -r)/build

all:
	$(MAKE) -C $(KDIR) M=$(CURDIR) modules

clean:
	$(MAKE) -C $(KDIR) M=$(CURDIR) clean

load: all
	sudo insmod hid-hp-zbook-kbd.ko

unload:
	sudo rmmod hid_hp_zbook_kbd
