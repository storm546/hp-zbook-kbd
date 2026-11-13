// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * HID driver for the per-key RGB keyboard backlight of the HP ZBook Studio G8
 * (board 8873, "Modena"). The LEDs are driven by an internal USB MCU (NXP,
 * 1fc9:2238). Interface 2 is a vendor HID collection with 64-byte reports that
 * speaks HP's "McuSDK2 / General" command set, the one OMEN Gaming Hub uses
 * for this keyboard (Mailroom + StarmadeKbLightingControl, "ModenaPerKeyRGB").
 *
 * Packet (64 bytes, unnumbered report):  cmd | index | len_lo | len_hi | payload
 * Reply  (64 bytes):  cmd | index | .. | .. | 0xEC | status  (0xAC ok, 0xFA fail)
 *
 * Exposes a multicolor LED "hp::kbd_backlight" plus "effect" and "key_colors"
 * attributes, and (hotkeys=1, default) handles:
 *   - the backlight key: static 100% -> 50% -> off -> 100%, effects on/off.
 *     The EC sends only a *break* of set-1 scancode 0x43 for it, which atkbd /
 *     the input core drop, so we catch it with an i8042 filter.
 *   - Right Alt + 0..9: select slot (0 = static colour, 1..9 = effects).
 *
 * See PROTOCOL.md for the full command list.
 */

#include <linux/completion.h>
#include <linux/delay.h>
#include <linux/dmi.h>
#include <linux/ctype.h>
#include <linux/hid.h>
#include <linux/i8042.h>
#include <linux/input.h>
#include <linux/kernel.h>
#include <linux/led-class-multicolor.h>
#include <linux/module.h>
#include <linux/mutex.h>
#include <linux/serio.h>
#include <linux/slab.h>
#include <linux/spinlock.h>
#include <linux/string.h>
#include <linux/usb.h>
#include <linux/version.h>
#include <linux/workqueue.h>

#define USB_VENDOR_ID_NXP		0x1fc9
#define USB_DEVICE_ID_HP_ZBOOK_KBD	0x2238

#define ZKBD_CTRL_IFACE		2
#define ZKBD_PKT_LEN		64
#define ZKBD_HDR_LEN		4
#define ZKBD_KEYS		144	/* what OMEN sends for Modena */
#define ZKBD_PAGE		60
#define ZKBD_TIMEOUT_MS		1000

enum {
	ZKBD_CMD_EFFECT		= 0x03,
	ZKBD_CMD_KEY_R		= 0x05,	/* G = 0x06, B = 0x07; index = page 0..2 */
	ZKBD_CMD_ONOFF		= 0x09,	/* payload 0x01 = START (on), 0x00 = OFF */
	ZKBD_CMD_DEVINFO	= 0x80,
};

static bool hotkeys = true;
module_param(hotkeys, bool, 0444);
MODULE_PARM_DESC(hotkeys, "Handle the backlight key and Right Alt + 0-9 (default: on)");

/* Other boards may share this MCU, but protocol and scan codes are unverified. */
static bool allow_unsupported;
module_param(allow_unsupported, bool, 0444);
MODULE_PARM_DESC(allow_unsupported,
	"Opt in unverified HP boards with the same MCU and report layout; no hotkeys");

static bool zkbd_tested_board(void)
{
	return (dmi_match(DMI_SYS_VENDOR, "HP") ||
		dmi_match(DMI_SYS_VENDOR, "Hewlett-Packard")) &&
		dmi_match(DMI_BOARD_NAME, "8873");
}

static bool zkbd_board_allowed(void)
{
	return zkbd_tested_board() || (allow_unsupported &&
		(dmi_match(DMI_SYS_VENDOR, "HP") ||
		 dmi_match(DMI_SYS_VENDOR, "Hewlett-Packard")));
}

/* Only unnumbered 64-byte input AND output reports speak this transport. */
static bool zkbd_reports_valid(struct hid_device *hdev)
{
	struct hid_report *report;
	int type, n;

	for (type = HID_INPUT_REPORT; type <= HID_OUTPUT_REPORT; type++) {
		n = 0;
		list_for_each_entry(report, &hdev->report_enum[type].report_list, list) {
			if (report->id || report->size != ZKBD_PKT_LEN * 8)
				return false;
			n++;
		}
		if (n != 1)
			return false;
	}
	return true;
}

/* McuSDK2 LightingEffectType + OMEN's defaults (KbAnimationDefaultSetting.json) */
struct zkbd_effect {
	const char *name;
	u8 id;
	u8 preset;	/* 0 = custom colour, else PresetType used as ShowMode */
	u32 color;
	u8 speed;
};

static const struct zkbd_effect zkbd_effects[] = {
	{ "wave",      10, 5, 0,        1 },
	{ "breathing",  2, 0, 0xea002a, 0 },
	{ "colorloop",  4, 2, 0,        1 },
	{ "sparkle",    7, 4, 0,        1 },
	{ "raindrop",  12, 0, 0x0ffa36, 2 },
	{ "ripple",     9, 4, 0,        1 },	/* reacts to typing */
	{ "ghosting",   8, 3, 0,        1 },	/* reacts to typing */
	{ "omenx",     13, 2, 0,        1 },
	{ "confetti",  15, 5, 0,        1 },
	{ "sun",       16, 5, 0,        1 },
	{ "swipe",     17, 5, 0,        1 },
};

/* Right Alt + N selects zkbd_slots[N]: -1 = static colour, else zkbd_effects[] index */
static const int zkbd_slots[10] = { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8 };

#define ZKBD_MODE_STATIC	-1

struct zkbd {
	struct hid_device *hdev;
	struct led_classdev_mc mc;
	struct mc_subled subled[3];

	struct mutex lock;		/* serialises command sequences and state */
	spinlock_t rlock;		/* protects expect/reply */
	struct completion done;
	u8 expect;
	u8 expect_idx;
	u8 reply[ZKBD_PKT_LEN];
	u8 *buf;			/* DMA-safe: report id + packet */

	u8 user[3][ZKBD_KEYS];		/* per-key base colours */
	u8 keys[3][ZKBD_KEYS];		/* scaled colours actually sent */
	bool perkey;
	int effect;			/* ZKBD_MODE_STATIC or zkbd_effects[] index */
	u32 effect_colors[6];
	int n_effect_colors;		/* 0 = effect default */

	/* hotkeys: filled in atomic context, consumed by hotkey_work */
	struct work_struct hotkey_work;
	atomic_t pending_cycles;
	atomic_t pending_slot;		/* -1 = none */
	struct input_handler ihandler;
	bool ihandler_registered;
	bool ralt;
	unsigned long swallow;		/* digits whose press we swallowed */
	bool i8042_filter;
	bool extended;
	bool f9_down;
};

static struct zkbd *zkbd_hotkey_dev;	/* i8042 filters have no context on older kernels */

/* ------------------------------------------------------------------ I/O */

static int zkbd_send(struct zkbd *z)
{
	int ret = hid_hw_output_report(z->hdev, z->buf, ZKBD_PKT_LEN + 1);

	if (ret == -ENOSYS)
		ret = hid_hw_raw_request(z->hdev, 0, z->buf, ZKBD_PKT_LEN + 1,
					 HID_OUTPUT_REPORT, HID_REQ_SET_REPORT);
	return ret < 0 ? ret : (ret == ZKBD_PKT_LEN + 1 ? 0 : -EIO);
}

/* Send one packet (header + @len payload bytes; @hdr_len = value for the length field). */
static int zkbd_xfer(struct zkbd *z, u8 cmd, u8 idx, u16 hdr_len,
		     const u8 *data, size_t len, bool check)
{
	unsigned long flags;
	int ret;

	lockdep_assert_held(&z->lock);
	if (len > ZKBD_PKT_LEN - ZKBD_HDR_LEN)
		return -EINVAL;

	memset(z->buf, 0, ZKBD_PKT_LEN + 1);
	z->buf[1] = cmd;		/* buf[0] = report id 0; usbhid strips it */
	z->buf[2] = idx;
	z->buf[3] = hdr_len & 0xff;
	z->buf[4] = hdr_len >> 8;
	if (len)
		memcpy(z->buf + 1 + ZKBD_HDR_LEN, data, len);

	reinit_completion(&z->done);
	spin_lock_irqsave(&z->rlock, flags);
	z->expect = cmd;
	z->expect_idx = idx;
	memset(z->reply, 0, sizeof(z->reply));
	spin_unlock_irqrestore(&z->rlock, flags);

	ret = zkbd_send(z);
	if (ret)
		goto out;
	if (!wait_for_completion_timeout(&z->done, msecs_to_jiffies(ZKBD_TIMEOUT_MS))) {
		ret = -ETIMEDOUT;
		goto out;
	}
	if (check && (z->reply[4] != 0xec || z->reply[5] != 0xac))
		ret = -EIO;
out:
	spin_lock_irqsave(&z->rlock, flags);
	z->expect = 0;
	spin_unlock_irqrestore(&z->rlock, flags);
	if (ret)
		hid_warn(z->hdev, "cmd %02x/%02x failed: %d (reply %*ph)\n",
			 cmd, idx, ret, 6, z->reply);
	return ret;
}

static int zkbd_power(struct zkbd *z, bool on)
{
	u8 v = on ? 0x01 : 0x00;	/* CommonToggleEnum START / OFF */

	return zkbd_xfer(z, ZKBD_CMD_ONOFF, 0, 1, &v, 1, true);
}

/* GeneralCommandHelper.SetKeyboardStaticLighting: 9 pages, length field 0 */
static int zkbd_write_static(struct zkbd *z)
{
	int c, p, ret;

	ret = zkbd_power(z, true);
	for (c = 0; !ret && c < 3; c++) {
		for (p = 0; !ret && p < 3; p++) {
			size_t off = p * ZKBD_PAGE;
			size_t len = min_t(size_t, ZKBD_PAGE, ZKBD_KEYS - off);

			ret = zkbd_xfer(z, ZKBD_CMD_KEY_R + c, p, 0,
					&z->keys[c][off], len, true);
		}
	}
	return ret;
}

/* Encoded like OMEN's StarmadeKbLightingEffectCommandHelper.SetLightingEffect */
static int zkbd_write_effect(struct zkbd *z)
{
	const struct zkbd_effect *e = &zkbd_effects[z->effect];
	u8 d[ZKBD_PKT_LEN - ZKBD_HDR_LEN] = { 0 }; /* full payload, including six colours */
	u32 colors[6];
	int n, i, ret;

	if (z->n_effect_colors) {
		n = z->n_effect_colors;
		memcpy(colors, z->effect_colors, sizeof(colors));
	} else if (!e->preset) {
		n = 1;
		colors[0] = e->color;
	} else {
		n = 0;
	}

	d[0] = e->id;
	if (n) {
		d[1] = n > 1;		/* ShowMode: single / multi colour */
		d[2] = n - 1;		/* ColorNumber */
	} else {
		d[1] = e->preset;	/* ShowMode = PresetType */
		d[2] = 4;		/* ColorNumber: preset */
	}
	d[3] = e->speed;	/* LedSpeed */
	d[4] = 3;		/* Brightness LV_04 (firmware animations ignore it) */
	d[5] = 3;		/* Direction: left to right */
	d[6] = 1;		/* RippleSize */
	d[7] = e->speed;	/* RaindropFrequency */
	for (i = 0; i < n; i++) {
		d[24 + 3 * i] = colors[i] >> 16;	/* packet offset 28 */
		d[25 + 3 * i] = colors[i] >> 8;
		d[26 + 3 * i] = colors[i];
	}

	ret = zkbd_power(z, true);
	return ret ?: zkbd_xfer(z, ZKBD_CMD_EFFECT, 0, 0x24, d, sizeof(d), true);
}

static int zkbd_apply(struct zkbd *z, enum led_brightness b)
{
	int c, k;

	lockdep_assert_held(&z->lock);

	if (b == LED_OFF)
		return zkbd_power(z, false);
	if (z->effect != ZKBD_MODE_STATIC)
		return zkbd_write_effect(z);

	led_mc_calc_color_components(&z->mc, b);
	for (c = 0; c < 3; c++) {
		if (z->perkey) {
			for (k = 0; k < ZKBD_KEYS; k++)
				z->keys[c][k] = z->user[c][k] * b /
						z->mc.led_cdev.max_brightness;
		} else {
			memset(z->keys[c], z->subled[c].brightness, ZKBD_KEYS);
		}
	}
	return zkbd_write_static(z);
}

static int zkbd_led_set(struct led_classdev *cdev, enum led_brightness b)
{
	struct zkbd *z = container_of(lcdev_to_mccdev(cdev), struct zkbd, mc);
	int ret;

	mutex_lock(&z->lock);
	ret = zkbd_apply(z, b);
	mutex_unlock(&z->lock);
	return ret;
}

/* Set brightness from inside the driver and tell userspace (UPower etc). */
static void zkbd_set_brightness_locked(struct zkbd *z, enum led_brightness b)
{
	if (zkbd_apply(z, b))
		return;
	z->mc.led_cdev.brightness = b;
#if IS_ENABLED(CONFIG_LEDS_BRIGHTNESS_HW_CHANGED)
	led_classdev_notify_brightness_hw_changed(&z->mc.led_cdev, b);
#endif
}

/* ------------------------------------------------------------- sysfs */

static struct zkbd *dev_to_zkbd(struct device *dev)
{
	struct led_classdev *cdev = dev_get_drvdata(dev);

	return container_of(lcdev_to_mccdev(cdev), struct zkbd, mc);
}

/*
 * key_colors: write "<index> <RRGGBB>" pairs (index 0..143, MCU LED order) or
 * "all <RRGGBB>". Switches to per-key static mode and applies.
 */
static char *zkbd_token(char **p)
{
	char *t;

	while ((t = strsep(p, " \t\r\n")) != NULL)
		if (*t)
			return t;
	return NULL;
}

static int zkbd_rgb(const char *s, u32 *rgb)
{
	int i;

	if (!s || strlen(s) != 6)
		return -EINVAL;
	for (i = 0; i < 6; i++)
		if (!isxdigit(s[i]))
			return -EINVAL;
	return kstrtou32(s, 16, rgb);
}

static ssize_t key_colors_store(struct device *dev, struct device_attribute *attr,
			       const char *buf, size_t count)
{
	struct zkbd *z = dev_to_zkbd(dev);
	u8 colors[3][ZKBD_KEYS];
	char *copy, *p, *idx, *token;
	u32 rgb;
	int from, to, i, ret = -EINVAL, pairs = 0;

	copy = kstrndup(buf, count, GFP_KERNEL);
	if (!copy)
		return -ENOMEM;
	p = copy;
	mutex_lock(&z->lock);
	memcpy(colors, z->user, sizeof(colors));
	while ((idx = zkbd_token(&p))) {
		token = zkbd_token(&p);
		if (zkbd_rgb(token, &rgb))
			goto out;
		from = 0;
		to = ZKBD_KEYS;
		if (strcmp(idx, "all")) {
			if (kstrtoint(idx, 10, &from) || from < 0 || from >= ZKBD_KEYS)
				goto out;
			to = from + 1;
		}
		for (i = from; i < to; i++) {
			colors[0][i] = rgb >> 16;
			colors[1][i] = rgb >> 8;
			colors[2][i] = rgb;
		}
		pairs++;
	}
	if (!pairs)
		goto out;
	memcpy(z->user, colors, sizeof(colors));
	z->perkey = true;
	z->effect = ZKBD_MODE_STATIC;
	if (!z->mc.led_cdev.brightness)
		z->mc.led_cdev.brightness = z->mc.led_cdev.max_brightness;
	ret = zkbd_apply(z, z->mc.led_cdev.brightness);
out:
	mutex_unlock(&z->lock);
	kfree(copy);
	return ret ?: count;
}
static DEVICE_ATTR_WO(key_colors);

static ssize_t effect_show(struct device *dev, struct device_attribute *attr,
			   char *buf)
{
	struct zkbd *z = dev_to_zkbd(dev);
	int i, len = 0;

	mutex_lock(&z->lock);
	len += sysfs_emit_at(buf, len, z->effect == ZKBD_MODE_STATIC ? "[static]" : "static");
	for (i = 0; i < ARRAY_SIZE(zkbd_effects); i++)
		len += sysfs_emit_at(buf, len, i == z->effect ? " [%s]" : " %s",
				     zkbd_effects[i].name);
	len += sysfs_emit_at(buf, len, "\n");
	mutex_unlock(&z->lock);
	return len;
}

/* "static" | "<name>" | "<name> RRGGBB [RRGGBB ...]" (up to 6 custom colours) */
static ssize_t effect_store(struct device *dev, struct device_attribute *attr,
			   const char *buf, size_t count)
{
	struct zkbd *z = dev_to_zkbd(dev);
	char *copy, *p, *name, *token;
	int i, ret = -EINVAL, effect = -2, ncol = 0;
	u32 cols[6] = { 0 };

	copy = kstrndup(buf, count, GFP_KERNEL);
	if (!copy)
		return -ENOMEM;
	p = copy;
	name = zkbd_token(&p);
	if (!name)
		goto free;
	if (!strcmp(name, "static"))
		effect = ZKBD_MODE_STATIC;
	for (i = 0; i < ARRAY_SIZE(zkbd_effects); i++)
		if (!strcmp(name, zkbd_effects[i].name))
			effect = i;
	if (effect == -2)
		goto free;
	while ((token = zkbd_token(&p))) {
		if (effect == ZKBD_MODE_STATIC || ncol == ARRAY_SIZE(cols) ||
		    zkbd_rgb(token, &cols[ncol]))
			goto free;
		ncol++;
	}
	mutex_lock(&z->lock);
	z->effect = effect;
	z->n_effect_colors = ncol;
	memcpy(z->effect_colors, cols, sizeof(cols));
	if (effect == ZKBD_MODE_STATIC)
		z->perkey = false;
	if (!z->mc.led_cdev.brightness)
		z->mc.led_cdev.brightness = z->mc.led_cdev.max_brightness;
	ret = zkbd_apply(z, z->mc.led_cdev.brightness);
	mutex_unlock(&z->lock);
free:
	kfree(copy);
	return ret ?: count;
}
static DEVICE_ATTR_RW(effect);

static struct attribute *zkbd_attrs[] = {
	&dev_attr_key_colors.attr,
	&dev_attr_effect.attr,
	NULL
};
ATTRIBUTE_GROUPS(zkbd);

/* ----------------------------------------------------------- hotkeys */

static void zkbd_hotkey_work(struct work_struct *work)
{
	struct zkbd *z = container_of(work, struct zkbd, hotkey_work);
	enum led_brightness max = z->mc.led_cdev.max_brightness;
	int cycles = atomic_xchg(&z->pending_cycles, 0);
	int slot = atomic_xchg(&z->pending_slot, -1);
	enum led_brightness b;

	mutex_lock(&z->lock);
	b = z->mc.led_cdev.brightness;

	if (slot >= 0) {
		z->effect = zkbd_slots[slot];
		z->n_effect_colors = 0;
		if (z->effect == ZKBD_MODE_STATIC)
			z->perkey = false;
		if (!b)
			b = max;
	}
	cycles %= z->effect == ZKBD_MODE_STATIC ? 3 : 2;
	while (cycles--) {
		if (!b)
			b = max;
		else if (z->effect == ZKBD_MODE_STATIC && b > max / 2)
			b = max / 2;
		else
			b = LED_OFF;	/* effects ignore brightness: just on/off */
	}

	zkbd_set_brightness_locked(z, b);
	mutex_unlock(&z->lock);
}

static void zkbd_queue_cycle(struct zkbd *z)
{
	atomic_inc(&z->pending_cycles);
	schedule_work(&z->hotkey_work);
}

/*
 * Backlight key: set-1 break 0xC3 with no preceding make 0x43. A real F9
 * (Fn+F9) sends both and is passed through untouched.
 */
static bool zkbd_i8042_filter_common(unsigned char data, unsigned char str)
{
	struct zkbd *z = READ_ONCE(zkbd_hotkey_dev);

	if (!z)
		return false;
	if (str & 0x20)		/* I8042_STR_AUXDATA: mouse byte */
		return false;
	if (data == 0xe0) {
		z->extended = true;
		return false;
	}
	if (z->extended) {
		z->extended = false;
		return false;
	}
	if (data == 0x43) {
		z->f9_down = true;
		return false;
	}
	if (data == 0xc3) {
		if (z->f9_down) {
			z->f9_down = false;
			return false;
		}
		if (z)
			zkbd_queue_cycle(z);
		return true;
	}
	return false;
}

#if LINUX_VERSION_CODE >= KERNEL_VERSION(6, 14, 0)
static bool zkbd_i8042_filter(unsigned char data, unsigned char str,
			      struct serio *port, void *context)
{
	return zkbd_i8042_filter_common(data, str);
}
#define zkbd_install_filter()	i8042_install_filter(zkbd_i8042_filter, NULL)
#else
static bool zkbd_i8042_filter(unsigned char data, unsigned char str,
			      struct serio *port)
{
	return zkbd_i8042_filter_common(data, str);
}
#define zkbd_install_filter()	i8042_install_filter(zkbd_i8042_filter)
#endif

/* Right Alt + number row: swallow the digit and select a slot. */
static bool zkbd_input_filter(struct input_handle *handle, unsigned int type,
			      unsigned int code, int value)
{
	struct zkbd *z = handle->handler->private;
	int digit;

	if (type != EV_KEY)
		return false;
	if (code == KEY_RIGHTALT) {
		WRITE_ONCE(z->ralt, value != 0);
		return false;
	}
	if (code < KEY_1 || code > KEY_0)
		return false;
	digit = code == KEY_0 ? 0 : code - KEY_1 + 1;

	if (value == 1 && READ_ONCE(z->ralt)) {
		set_bit(digit, &z->swallow);
		atomic_set(&z->pending_slot, digit);
		schedule_work(&z->hotkey_work);
		return true;
	}
	if (test_bit(digit, &z->swallow)) {	/* matching repeat / release */
		if (value == 0)
			clear_bit(digit, &z->swallow);
		return true;
	}
	return false;
}

static int zkbd_input_connect(struct input_handler *handler, struct input_dev *dev,
			      const struct input_device_id *id)
{
	struct input_handle *handle;
	int err;

	/* Only the built-in PS/2 keyboard, never external keyboards. */
	if (dev->id.bustype != BUS_I8042 || !test_bit(KEY_RIGHTALT, dev->keybit) || !test_bit(KEY_1, dev->keybit))
		return -ENODEV;

	handle = kzalloc(sizeof(*handle), GFP_KERNEL);
	if (!handle)
		return -ENOMEM;
	handle->dev = dev;
	handle->handler = handler;
	handle->name = "hp-zbook-kbd";

	err = input_register_handle(handle);
	if (err)
		goto err_free;
	err = input_open_device(handle);
	if (err)
		goto err_unregister;
	return 0;

err_unregister:
	input_unregister_handle(handle);
err_free:
	kfree(handle);
	return err;
}

static void zkbd_input_disconnect(struct input_handle *handle)
{
	input_close_device(handle);
	input_unregister_handle(handle);
	kfree(handle);
}

static const struct input_device_id zkbd_input_ids[] = {
	{ .flags = INPUT_DEVICE_ID_MATCH_EVBIT, .evbit = { BIT_MASK(EV_KEY) } },
	{ }
};

static void zkbd_hotkeys_start(struct zkbd *z)
{
	int ret;

	INIT_WORK(&z->hotkey_work, zkbd_hotkey_work);
	atomic_set(&z->pending_cycles, 0);
	atomic_set(&z->pending_slot, -1);
	if (!hotkeys || !zkbd_tested_board())
		return;

	WRITE_ONCE(zkbd_hotkey_dev, z);
	ret = zkbd_install_filter();
	if (ret)
		hid_warn(z->hdev, "i8042 filter unavailable (%d): backlight key not handled\n", ret);
	z->i8042_filter = !ret;

	z->ihandler.filter = zkbd_input_filter;
	z->ihandler.connect = zkbd_input_connect;
	z->ihandler.disconnect = zkbd_input_disconnect;
	z->ihandler.name = "hp-zbook-kbd";
	z->ihandler.id_table = zkbd_input_ids;
	z->ihandler.private = z;
	ret = input_register_handler(&z->ihandler);
	if (ret)
		hid_warn(z->hdev, "input handler failed (%d): Right Alt + digit not handled\n", ret);
	z->ihandler_registered = !ret;
}

static void zkbd_hotkeys_stop(struct zkbd *z)
{
	if (z->ihandler_registered)
		input_unregister_handler(&z->ihandler);
	if (z->i8042_filter)
		i8042_remove_filter(zkbd_i8042_filter);
	WRITE_ONCE(zkbd_hotkey_dev, NULL);
	cancel_work_sync(&z->hotkey_work);
}

/* ------------------------------------------------------------ HID glue */

static int zkbd_raw_event(struct hid_device *hdev, struct hid_report *report,
			  u8 *data, int size)
{
	struct zkbd *z = hid_get_drvdata(hdev);
	unsigned long flags;

	if (!z || size < 6)
		return 0;

	spin_lock_irqsave(&z->rlock, flags);
	if (z->expect && size == ZKBD_PKT_LEN && data[0] == z->expect &&
	    data[1] == z->expect_idx &&
	    (z->expect == ZKBD_CMD_DEVINFO ||
	     (data[4] == 0xec && (data[5] == 0xac || data[5] == 0xfa)))) {
		memcpy(z->reply, data, min(size, ZKBD_PKT_LEN));
		z->expect = 0;
		complete(&z->done);
	}
	spin_unlock_irqrestore(&z->rlock, flags);
	return 0;
}

static int zkbd_probe(struct hid_device *hdev, const struct hid_device_id *id)
{
	struct usb_interface *intf;
	struct led_classdev *cdev;
	struct zkbd *z;
	int ret, i;

	ret = hid_parse(hdev);
	if (ret)
		return ret;

	/* Only claim the lighting interface; the rest is handled normally. */
	if (!hid_is_usb(hdev))
		return -ENODEV;
	intf = to_usb_interface(hdev->dev.parent);
	if (!zkbd_board_allowed() ||
	    intf->cur_altsetting->desc.bInterfaceNumber != ZKBD_CTRL_IFACE)
		return hid_hw_start(hdev, HID_CONNECT_DEFAULT);

	if (!zkbd_reports_valid(hdev)) {
		hid_err(hdev, "incompatible HID reports: need unnumbered 64-byte input/output\n");
		return -ENODEV;
	}
	if (!zkbd_tested_board())
		hid_warn(hdev, "experimental board: protocol/key map unverified, hotkeys disabled\n");

	z = devm_kzalloc(&hdev->dev, sizeof(*z), GFP_KERNEL);
	if (!z)
		return -ENOMEM;
	z->buf = devm_kzalloc(&hdev->dev, ZKBD_PKT_LEN + 1, GFP_KERNEL);
	if (!z->buf)
		return -ENOMEM;

	z->hdev = hdev;
	z->effect = ZKBD_MODE_STATIC;
	mutex_init(&z->lock);
	spin_lock_init(&z->rlock);
	init_completion(&z->done);
	memset(z->user, 0xff, sizeof(z->user));
	hid_set_drvdata(hdev, z);

	ret = hid_hw_start(hdev, HID_CONNECT_HIDRAW);
	if (ret)
		return ret;
	ret = hid_hw_open(hdev);	/* keep the IN endpoint polled for replies */
	if (ret)
		goto err_stop;
	hid_device_io_start(hdev);

	mutex_lock(&z->lock);
	ret = zkbd_xfer(z, ZKBD_CMD_DEVINFO, 1, 0, NULL, 0, false);
	if (!ret)
		hid_info(hdev, "keyboard MCU info %*ph\n", 16, z->reply);
	mutex_unlock(&z->lock);
	if (ret) {
		hid_err(hdev, "keyboard MCU did not answer, not binding\n");
		ret = -ENODEV;
		goto err_close;
	}

	for (i = 0; i < 3; i++) {
		z->subled[i].color_index = LED_COLOR_ID_RED + i;
		z->subled[i].channel = i;
		z->subled[i].intensity = 255;
	}
	z->mc.subled_info = z->subled;
	z->mc.num_colors = 3;
	cdev = &z->mc.led_cdev;
	cdev->name = "hp::kbd_backlight";
	cdev->max_brightness = 255;
	cdev->brightness = 255;
	cdev->brightness_set_blocking = zkbd_led_set;
	cdev->flags = 0; /* HID resume restores state after USB transport resumes. */
	if (IS_ENABLED(CONFIG_LEDS_BRIGHTNESS_HW_CHANGED))
		cdev->flags |= LED_BRIGHT_HW_CHANGED;	/* desktops see hotkey changes */

	ret = led_classdev_multicolor_register(&hdev->dev, &z->mc);
	if (ret)
		goto err_close;

	/* Multicolor registration replaces cdev->groups; add ours afterwards. */
	ret = sysfs_create_groups(&cdev->dev->kobj, zkbd_groups);
	if (ret) {
		led_classdev_multicolor_unregister(&z->mc);
		goto err_close;
	}
	kobject_uevent(&cdev->dev->kobj, KOBJ_CHANGE);
	zkbd_hotkeys_start(z);
	return 0;

err_close:
	hid_hw_close(hdev);
err_stop:
	hid_hw_stop(hdev);
	return ret;
}

static void zkbd_remove(struct hid_device *hdev)
{
	struct zkbd *z = hid_get_drvdata(hdev);

	if (z) {
		zkbd_hotkeys_stop(z);
		sysfs_remove_groups(&z->mc.led_cdev.dev->kobj, zkbd_groups);
		led_classdev_multicolor_unregister(&z->mc);
		hid_hw_close(hdev);
	}
	hid_hw_stop(hdev);
}

#ifdef CONFIG_PM
static int zkbd_reset_resume(struct hid_device *hdev)
{
	struct zkbd *z = hid_get_drvdata(hdev);
	int ret = 0;

	if (z) {
		mutex_lock(&z->lock);
		WRITE_ONCE(z->ralt, false);
		for (int digit = 0; digit < 10; digit++)
			clear_bit(digit, &z->swallow);
		ret = zkbd_apply(z, z->mc.led_cdev.brightness);
		mutex_unlock(&z->lock);
	} else if (hdev->claimed & HID_CLAIMED_INPUT) {
		hidinput_reset_resume(hdev);
	}
	return ret;
}
#endif

static const struct hid_device_id zkbd_devices[] = {
	{ HID_USB_DEVICE(USB_VENDOR_ID_NXP, USB_DEVICE_ID_HP_ZBOOK_KBD) },
	{ }
};
MODULE_DEVICE_TABLE(hid, zkbd_devices);

/* hid-generic defers every interface for an ID in our table. Keep generic
 * HID handling on non-lighting interfaces so their input devices still work. */
static bool zkbd_match(struct hid_device *hdev, bool ignore_special_driver)
{
	/* Keep generic handling even when lighting support is not enabled. */
	return hid_is_usb(hdev);
}

static struct hid_driver zkbd_driver = {
	.name		= "hp-zbook-kbd",
	.id_table	= zkbd_devices,
	.match		= zkbd_match,
	.probe		= zkbd_probe,
	.remove		= zkbd_remove,
	.raw_event	= zkbd_raw_event,
#ifdef CONFIG_PM
	.resume		= zkbd_reset_resume,
	.reset_resume	= zkbd_reset_resume,
#endif
};
module_hid_driver(zkbd_driver);

MODULE_DESCRIPTION("HP ZBook Studio G8 per-key RGB keyboard backlight");
MODULE_LICENSE("GPL");
MODULE_VERSION("0.4");
MODULE_SOFTDEP("pre: i8042");
