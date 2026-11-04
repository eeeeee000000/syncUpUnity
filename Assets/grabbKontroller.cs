using Unity.Mathematics.Geometry;
using UnityEngine;

public enum Directions
{
    Up,
    Down,
    Left,
    Right,
    Total
}

public class PlayerControllerBeta : MonoBehaviour
{

    public float speed = 5.0f;


    public GameObject BulletPrefab = null;

    void Update()
    {

        if (Input.GetKeyDown(KeyCode.Space))
        {
            GameObject.Instantiate(BulletPrefab, transform.position, Quaternion.identity);
        }

        //Up
        if (Input.GetKey(KeyCode.W))
        {
            transform.position += Vector3.up * speed * Time.deltaTime;
        }
        //Down
        if (Input.GetKey(KeyCode.S))
        {
            transform.position += Vector3.up * -speed * Time.deltaTime;
        }
        //Right
        if (Input.GetKey(KeyCode.D))
        {
            transform.position += Vector3.right * speed * Time.deltaTime;
        }
        //Left
        if (Input.GetKey(KeyCode.A))
        {
            transform.position += Vector3.right * -speed * Time.deltaTime;
        }
    }

}