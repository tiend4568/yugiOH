using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance;

    [Serializable]
    public class SFXEntry
    {
        public string id;       // Tên gọi hiệu ứng, ví dụ: "Click"
        public AudioClip clip;
    }

    [Header("Audio Source (Đóng vai trò loa phát)")]
    public AudioSource musicSource;   // Loa nhạc nền
    public AudioSource sfxSource;     // Loa hiệu ứng (PHẢI khác loa nhạc)

    [Header("Danh sách nhạc nền")]
    public List<AudioClip> lobbyMusic = new List<AudioClip>();    // Nhạc ở sảnh chờ
    public List<AudioClip> battleMusic = new List<AudioClip>();   // Nhạc khi chiến đấu

    [Header("Danh sách SFX")]
    public List<SFXEntry> sfxList = new List<SFXEntry>();

    [Header("Âm lượng")]
    [Range(0f, 1f)] public float musicVolume = 0.6f;
    [Range(0f, 1f)] public float sfxVolume = 1f;

    const string KEY_MUSIC = "MusicOn";
    const string KEY_SFX = "SFXOn";

    public bool IsMusicOn { get; private set; } = true;
    public bool IsSFXOn { get; private set; } = true;

    Dictionary<string, AudioClip> sfxMap;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        SetupSources();
        BuildSfxMap();

        IsMusicOn = PlayerPrefs.GetInt(KEY_MUSIC, 1) == 1;
        IsSFXOn = PlayerPrefs.GetInt(KEY_SFX, 1) == 1;

        musicSource.mute = !IsMusicOn;
        sfxSource.mute = !IsSFXOn;
        musicSource.volume = musicVolume;
        sfxSource.volume = sfxVolume;
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // Tự động nhận diện Scene để đổi nhạc phù hợp
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "BattleScene")
        {
            PlayBattleMusic();
        }
        else if (scene.name == "SampleScene")
        {
            PlayLobbyMusic();
        }
    }

    void Start()
    {
        PlayLobbyMusic();
    }

    void SetupSources()
    {
        if (musicSource == null)
            musicSource = gameObject.AddComponent<AudioSource>();

        if (sfxSource == null || sfxSource == musicSource)
        {
            sfxSource = gameObject.AddComponent<AudioSource>();
        }

        musicSource.playOnAwake = false;
        musicSource.loop = true;
        sfxSource.playOnAwake = false;
        sfxSource.loop = false;
    }

    void BuildSfxMap()
    {
        sfxMap = new Dictionary<string, AudioClip>();
        foreach (var e in sfxList)
        {
            if (e == null || string.IsNullOrEmpty(e.id) || e.clip == null) continue;
            sfxMap[e.id] = e.clip;
        }
    }

    void PrepareMusic(List<AudioClip> list)
    {
        if (musicSource == null || list == null || list.Count == 0) return;

        // Nếu danh sách mới không chứa bài đang phát thì mới đổi bài ngẫu nhiên
        if (musicSource.clip == null || !list.Contains(musicSource.clip))
        {
            AudioClip pick = list[UnityEngine.Random.Range(0, list.Count)];
            musicSource.clip = pick;
            musicSource.loop = true;
        }
    }

    public void PlayLobbyMusic()
    {
        PrepareMusic(lobbyMusic);
        if (IsMusicOn && musicSource != null && !musicSource.isPlaying)
        {
            musicSource.Play();
        }
    }

    public void PlayBattleMusic()
    {
        PrepareMusic(battleMusic);
        if (IsMusicOn && musicSource != null && !musicSource.isPlaying)
        {
            musicSource.Play();
        }
    }

    public void StopMusic()
    {
        if (musicSource != null) musicSource.Stop();
    }

    public void ToggleMusic(bool isOn)
    {
        IsMusicOn = isOn;
        if (musicSource != null)
        {
            musicSource.mute = !isOn;
            if (isOn && !musicSource.isPlaying && musicSource.clip != null)
            {
                musicSource.Play();
            }
        }
        PlayerPrefs.SetInt(KEY_MUSIC, isOn ? 1 : 0);
        PlayerPrefs.Save();
    }

    public void ToggleSFX(bool isOn)
    {
        IsSFXOn = isOn;
        if (sfxSource != null) sfxSource.mute = !isOn;
        PlayerPrefs.SetInt(KEY_SFX, isOn ? 1 : 0);
        PlayerPrefs.Save();
    }

    public void PlaySFX(string id)
    {
        if (sfxMap != null && sfxMap.TryGetValue(id, out AudioClip clip))
            PlaySFX(clip);
        else
            Debug.LogWarning($"SoundManager: không tìm thấy SFX có id là \"{id}\" trong sfxList.");
    }

    public void PlaySFX(AudioClip clip)
    {
        if (clip == null || sfxSource == null) return;
        sfxSource.PlayOneShot(clip, sfxVolume);
    }

    public void PlayDefaultClick() => PlaySFX("Click");
}